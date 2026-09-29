// メモリモデル — sm83_subset/full が外部バス (addr → data_in, mem_write) を持つための
// 純 Rust 実装 (TODO Step B)。GB ライクな配置: ブート ROM 0x0000-0x00FF (0xFF50 で解除)、
// ROM 0x0000..、VRAM 0x8000-0x9FFF、RAM 0xC000..、I/O (IF/HRAM/IE/0xFF00-0xFF7F、LY は M サイクルから算出)。
//
// F# の Testbench.fs (MemoryImage) と同じ規則にする (DESIGN-VERIFY.md §5.3):
//   読出: ブート ROM (マップ中かつ 0x0000-0x00FF) → ROM (0 から ROM 長) → VRAM → RAM 窓 →
//         IF (0xFF0F) / IE (0xFFFF) / HRAM (0xFF80-0xFFFE) / I/O 0xFF00-0xFF7F → 0xFF
//   書込: VRAM / RAM 窓 / I/O のみ (ROM と LY への書込は無視)。RAM 窓が I/O と重なる場合は RAM 窓が優先
//   割込み: CPU の irq 入力 = IE & IF & 0x1F、int_ack のビットを IF から下ろす
//
// ブート ROM は 0xFF50 に bit0=1 を書くと解除される (0x0000-0x00FF が ROM に戻る)。
// LY (0xFF44) は読み出し専用で、set_lcd_y が M サイクル番号から算出して書き込む
// (ホスト側 = TB / runner が周期の先頭で呼ぶ契約)。
use anyhow::Result;

pub const INTERRUPT_FLAG_ADDR: u16 = 0xFF0F;
pub const INTERRUPT_ENABLE_ADDR: u16 = 0xFFFF;
pub const HRAM_BASE: u16 = 0xFF80;
pub const HRAM_SIZE: usize = 0x7F;
pub const VRAM_BASE: u16 = 0x8000;
pub const VRAM_SIZE: usize = 0x2000;
pub const IO_BASE: u16 = 0xFF00;
pub const IO_SIZE: usize = 0x80;
/// LY (0xFF44)。読み出し専用で M サイクル番号から算出する
pub const LCD_Y_ADDR: u16 = 0xFF44;
/// ブート ROM のマップ解除 (0xFF50 に 1 を書く)
pub const BOOT_ROM_DISABLE_ADDR: u16 = 0xFF50;
/// 1 フレーム = 154 ライン × 456 サイクル (LY の算出用)
pub const FRAME_CYCLES: u32 = 70224;
pub const LINE_CYCLES: u32 = 456;

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
    /// 0x8000-0x9FFF。ブート ROM がロゴを書く先
    pub vram: Vec<u8>,
    /// 0xFF00-0xFF7F のレジスタ。未使用分は 0xFF で初期化 (従来の「未モデルは 0xFF」挙動と同じ)
    pub io: Vec<u8>,
    /// 0x0000-0x00FF に重ねるブート ROM (None なら重ねない)
    pub boot_rom: Option<Vec<u8>>,
    /// ブート ROM のマップ状態。0xFF50 に 1 を書くと解除される
    pub boot_rom_enabled: bool,
    /// 命令実行による書き込みが発生したか (デバッグ/統計用)
    pub write_count: u32,
}

impl Memory {
    pub fn new(rom: Vec<u8>, config: MemoryConfig) -> Self {
        let mut io = vec![0xFFu8; IO_SIZE];
        io[(LCD_Y_ADDR - IO_BASE) as usize] = 0;
        Self {
            rom,
            ram: vec![0u8; config.ram_size],
            config,
            hram: vec![0u8; HRAM_SIZE],
            interrupt_flag: 0,
            interrupt_enable: 0,
            vram: vec![0u8; VRAM_SIZE],
            io,
            boot_rom: None,
            boot_rom_enabled: false,
            write_count: 0,
        }
    }

    /// ブート ROM を 0x0000-0x00FF に重ねたメモリを作る (0xFF50 への書込で解除される)。
    pub fn with_boot_rom(rom: Vec<u8>, boot_rom: Vec<u8>, config: MemoryConfig) -> Self {
        let mut m = Self::new(rom, config);
        m.boot_rom = Some(boot_rom);
        m.boot_rom_enabled = true;
        m
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

    fn io_offset(addr: u16) -> Option<usize> {
        let a = addr as usize;
        let base = IO_BASE as usize;
        (a >= base && a < base + IO_SIZE).then(|| a - base)
    }

    pub fn read(&self, addr: u16) -> u8 {
        let a = addr as usize;
        if let Some(boot) = &self.boot_rom {
            if self.boot_rom_enabled && a < boot.len() {
                return boot[a];
            }
        }
        if a < self.rom.len() {
            return self.rom[a];
        }
        if a >= VRAM_BASE as usize && a < VRAM_BASE as usize + VRAM_SIZE {
            return self.vram[a - VRAM_BASE as usize];
        }
        if let Some(i) = self.ram_offset(addr) {
            return self.ram[i];
        }
        match addr {
            INTERRUPT_FLAG_ADDR => self.interrupt_flag,
            INTERRUPT_ENABLE_ADDR => self.interrupt_enable,
            _ => {
                if let Some(i) = Self::hram_offset(addr) {
                    return self.hram[i];
                }
                if let Some(i) = Self::io_offset(addr) {
                    return self.io[i];
                }
                0xFF
            }
        }
    }

    pub fn write(&mut self, addr: u16, value: u8) {
        let a = addr as usize;
        if a >= VRAM_BASE as usize && a < VRAM_BASE as usize + VRAM_SIZE {
            self.vram[a - VRAM_BASE as usize] = value;
            self.write_count += 1;
            return;
        }
        if let Some(i) = self.ram_offset(addr) {
            self.ram[i] = value;
            self.write_count += 1;
            return;
        }
        match addr {
            INTERRUPT_FLAG_ADDR => self.interrupt_flag = value,
            INTERRUPT_ENABLE_ADDR => self.interrupt_enable = value,
            BOOT_ROM_DISABLE_ADDR => self.boot_rom_enabled = (value & 1) == 0,
            // LY (0xFF44) は読み出し専用: 書込は無視する
            LCD_Y_ADDR => {}
            _ => {
                if let Some(i) = Self::hram_offset(addr) {
                    self.hram[i] = value;
                } else if let Some(i) = Self::io_offset(addr) {
                    self.io[i] = value;
                }
                // ROM やそれ以外への書き込みは無視 (バスに書かれても握りつぶす)
            }
        }
    }

    /// M サイクル番号から LY (0xFF44) を更新する (1 フレーム = 154 ライン × 456 サイクル)。
    /// ホスト側 (TB / runner) が周期の先頭で呼ぶ。
    pub fn set_lcd_y(&mut self, cycle: u32) {
        let ly = ((cycle % FRAME_CYCLES) / LINE_CYCLES) as u8;
        self.io[(LCD_Y_ADDR - IO_BASE) as usize] = ly;
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
    fn reads_rom_ram_vram_and_open_bus() {
        let m = memory();
        assert_eq!(m.read(0x0123), 0x23);
        assert_eq!(m.read(0xC000), 0x00);
        assert_eq!(m.read(0x8000), 0x00); // VRAM (初期 0)
        assert_eq!(m.read(0x9FFF), 0x00);
        assert_eq!(m.read(0xFE00), 0xFF); // OAM は未モデル
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
    fn vram_and_io_registers_keep_written_values() {
        let mut m = memory();
        m.write(0x8010, 0xAB);
        m.write(0xFF40, 0x91); // LCDC
        m.write(0xFF42, 0x64); // SCY
        assert_eq!(m.read(0x8010), 0xAB);
        assert_eq!((m.read(0xFF40), m.read(0xFF42)), (0x91, 0x64));
        assert_eq!(m.read(0xFF41), 0xFF); // 未使用レジスタは 0xFF のまま
    }

    #[test]
    fn lcd_y_is_read_only_and_advances_with_cycles() {
        let mut m = memory();
        assert_eq!(m.read(0xFF44), 0x00);
        m.write(0xFF44, 0x99); // 書込は無視
        assert_eq!(m.read(0xFF44), 0x00);
        for (cycle, ly) in [(0u32, 0u8), (455, 0), (456, 1), (70223, 153), (70224, 0), (70380, 0)] {
            m.set_lcd_y(cycle);
            assert_eq!(m.read(0xFF44), ly, "cycle {cycle}");
        }
    }

    #[test]
    fn boot_rom_overlays_until_unmapped() {
        let rom: Vec<u8> = (0..0x200u32).map(|i| i as u8).collect();
        let boot: Vec<u8> = (0..0x100u32).map(|i| (0x40 + (i & 0x3F)) as u8).collect();
        let mut m = Memory::with_boot_rom(rom, boot, MemoryConfig::default());
        assert!(m.boot_rom_enabled);
        assert_eq!(m.read(0x0000), 0x40);
        assert_eq!(m.read(0x00FF), 0x7F);
        assert_eq!(m.read(0x0100), 0x00); // 0x0100 からは ROM
        m.write(0xFF50, 1);
        assert!(!m.boot_rom_enabled);
        assert_eq!(m.read(0x0000), 0x00);
        assert_eq!(m.read(0x00FF), 0xFF);
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
