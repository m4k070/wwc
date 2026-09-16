// メモリモデル — sm83_subset/full が外部バス (addr → data_in, mem_write) を持つための
// 純 Rust 実装 (TODO Step B)。GB ライクな配置: ROM 0x0000..、RAM 0xC000..。
use anyhow::Result;

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
    /// 命令実行による書き込みが発生したか (デバッグ/統計用)
    pub write_count: u32,
}

impl Memory {
    pub fn new(rom: Vec<u8>, config: MemoryConfig) -> Self {
        Self { rom, ram: vec![0u8; config.ram_size], config, write_count: 0 }
    }

    pub fn read(&self, addr: u16) -> u8 {
        let a = addr as usize;
        if a < self.rom.len() {
            self.rom[a]
        } else if (addr >= self.config.ram_base)
            && (a < self.config.ram_base as usize + self.ram.len())
        {
            self.ram[a - self.config.ram_base as usize]
        } else {
            0xFF
        }
    }

    pub fn write(&mut self, addr: u16, value: u8) {
        let a = addr as usize;
        let base = self.config.ram_base as usize;
        if (addr >= self.config.ram_base) && (a < base + self.ram.len()) {
            let i = a - base;
            self.ram[i] = value;
            self.write_count += 1;
        }
        // ROM への書き込みは無視 (バスに書かれても握りつぶす)
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
