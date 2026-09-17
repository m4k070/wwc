// メモリモデル — sm83_subset/full が外部バス (addr → data_in, mem_write) を持つための
// 純 Rust 実装 (TODO Step B)。GB ライクな配置: ROM 0x0000..、RAM 0xC000..、I/O (IF/HRAM/IE)。
//
// F# の Testbench.fs (MemoryImage) と同じ規則にする (DESIGN-VERIFY.md §5.3):
//   読出: ROM (0 から ROM 長) → RAM 窓 → IF (0xFF0F) / HRAM (0xFF80-0xFFFE) / IE (0xFFFF) → 0xFF
//   書込: RAM 窓 → IF / HRAM / IE (ROM への書込は無視)。RAM 窓が I/O と重なる場合は RAM 窓が優先
//   割込み: CPU の irq 入力 = IE & IF & 0x1F、int_ack のビットを IF から下ろす
use anyhow::Result;

pub const INTERRUPT_FLAG_ADDR: u16 = 0xFF0F;
pub const INTERRUPT_ENABLE_ADDR: u16 = 0xFFFF;
pub const HRAM_BASE: u16 = 0xFF80;
pub const HRAM_SIZE: usize = 0x7F;

#[derive(Debug, Clone, Copy)]
pub struct MemoryConfig {
    /// RAM ベースアドレス (既定: 0xC000、GB の WRAM 位置に合わせる)
    pub ram_base: u16,
    /// RAM サイズ (bytes)。0xC000..0xC000+ram_size
    pub ram_size: usize,
}

impl Default for MemoryConfig {
    fn default() -> Self {
        Self { ram_base: 0xC000, ram_size: 8192 }
    }
}

pub struct Memory {
    pub rom: Vec<u8>,
    pub ram: Vec<u8>,
    pub config: MemoryConfig,
    /// 0xFF80-0xFFFE
    pub hram: Vec<u8>,
    /// IF (0xFF0F)。書いたバイトをそのまま保持する (F# / gbfs と同じ)
    pub interrupt_flag: u8,
    /// IE (0xFFFF)
    pub interrupt_enable: u8,
    /// 命令実行による書き込みが発生したか (デバッグ/統計用)
    pub write_count: u32,
}

impl Memory {
    pub fn new(rom: Vec<u8>, config: MemoryConfig) -> Self {
        Self {
            rom,
            ram: vec![0u8; config.ram_size],
            config,
            hram: vec![0u8; HRAM_SIZE],
            interrupt_flag: 0,
            interrupt_enable: 0,
            write_count: 0,
        }
    }

    fn ram_offset(&self, addr: u16) -> Option<usize> {
        let a = addr as usize;
        let base = self.config.ram_base as usize;
        (a >= base && a < base + self.ram.len()).then(|| a - base)
    }

    fn hram_offset(addr: u16) -> Option<usize> {
        let a = addr as usize;
        let base = HRAM_BASE as usize;
        (a >= base && a < base + HRAM_SIZE).then(|| a - base)
    }

    pub fn read(&self, addr: u16) -> u8 {
        let a = addr as usize;
        if a < self.rom.len() {
            return self.rom[a];
        }
        if let Some(i) = self.ram_offset(addr) {
            return self.ram[i];
        }
        match addr {
            INTERRUPT_FLAG_ADDR => self.interrupt_flag,
            INTERRUPT_ENABLE_ADDR => self.interrupt_enable,
            _ => Self::hram_offset(addr).map(|i| self.hram[i]).unwrap_or(0xFF),
        }
    }

    pub fn write(&mut self, addr: u16, value: u8) {
        if let Some(i) = self.ram_offset(addr) {
            self.ram[i] = value;
            self.write_count += 1;
            return;
        }
        match addr {
            INTERRUPT_FLAG_ADDR => self.interrupt_flag = value,
            INTERRUPT_ENABLE_ADDR => self.interrupt_enable = value,
            _ => {
                if let Some(i) = Self::hram_offset(addr) {
                    self.hram[i] = value;
                }
                // ROM やそれ以外への書き込みは無視 (バスに書かれても握りつぶす)
            }
        }
    }

    /// CPU の irq 入力に渡す割込み要因 (IE & IF の下位 5bit)。
    pub fn pending_interrupts(&self) -> u8 {
        self.interrupt_enable & self.interrupt_flag & 0x1F
    }

    /// CPU が受け付けた割込み (int_ack の one-hot) のビットを IF から下ろす。
    pub fn acknowledge_interrupts(&mut self, ack: u8) {
        self.interrupt_flag &= !ack;
    }
}

/// ROM/プログラムイメージのロード。VRAM 等 sm83 固有仕様は無視する単純モデル。
pub enum RomSource {
    /// ファイルパス (生バイト列)
    Path(std::path::PathBuf),
    /// base64 エンコードデータ
    Base64(String),
}

impl RomSource {
    pub fn load(&self) -> Result<Vec<u8>> {
        match self {
            Self::Path(p) => {
                let data = std::fs::read(p)?;
                Ok(data)
            }
            Self::Base64(s) => {
                use base64::Engine;
                let v = base64::engine::general_purpose::STANDARD.decode(s.trim())?;
                Ok(v)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn memory() -> Memory {
        Memory::new((0..0x200u32).map(|i| i as u8).collect(), MemoryConfig::default())
    }

    #[test]
    fn reads_rom_ram_and_open_bus() {
        let m = memory();
        assert_eq!(m.read(0x0123), 0x23);
        assert_eq!(m.read(0xC000), 0x00);
        assert_eq!(m.read(0x8000), 0xFF);
    }

    #[test]
    fn io_holds_interrupt_registers_and_hram() {
        let mut m = memory();
        m.write(0xFF0F, 0x06);
        m.write(0xFFFF, 0x05);
        m.write(0xFF90, 0x77);
        m.write(0x0010, 0x99); // ROM への書込は無視
        assert_eq!((m.read(0xFF0F), m.read(0xFFFF), m.read(0xFF90)), (0x06, 0x05, 0x77));
        assert_eq!(m.read(0xFF10), 0xFF);
        assert_eq!(m.read(0x0010), 0x10);
    }

    #[test]
    fn pending_is_ie_and_if_and_ack_clears_one_bit() {
        let mut m = memory();
        m.write(0xFF0F, 0xE6);
        m.write(0xFFFF, 0x07);
        assert_eq!(m.pending_interrupts(), 0x06);
        m.acknowledge_interrupts(0x02);
        assert_eq!(m.interrupt_flag, 0xE4);
    }

    #[test]
    fn ram_window_overlapping_io_takes_precedence() {
        let mut m = Memory::new(vec![], MemoryConfig { ram_base: 0xF000, ram_size: 4096 });
        m.write(0xFF0F, 0x1F);
        assert_eq!(m.read(0xFF0F), 0x1F);
        assert_eq!(m.interrupt_flag, 0);
    }
}
