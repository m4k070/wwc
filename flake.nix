{
  description = "WireWorld HDL compiler dev environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = nixpkgs.legacyPackages.${system};
        # Playwright Chromium が実行時に必要とする共有ライブラリ群
        chromiumLibs = with pkgs; [
          glib nss nspr dbus at-spi2-core cups expat libxcb libX11 libXext
          libxkbcommon cairo pango udev libxkbfile libxcomposite libxdamage
          libxfixes libxrandr libgbm alsa-lib pulseaudio
        ];
        # wgpu-runner が Vulkan 経由で GPU を使うためのライブラリ
        vulkanLibs = with pkgs; [ vulkan-loader ];
        # 全ランタイムライブラリのパス (Chromium + Vulkan)
        allLibs = chromiumLibs ++ vulkanLibs;
      in {
        devShells.default = pkgs.mkShell {
          name = "wwc";
          packages = with pkgs; [
            dotnet-sdk_8
            python3
            yosys
            rustc cargo rustfmt rust-analyzer
            nodejs_22
            vulkan-tools
            git
          ] ++ allLibs;

          # Chromium 用 (web/run-test.sh が参照)
          WWC_CHROMIUM_LIBS = pkgs.lib.makeLibraryPath chromiumLibs;

          # wgpu-runner が Vulkan を認識するためのライブラリパス
          LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (with pkgs; [ vulkan-loader ]);

          shellHook = ''
            echo "wwc dev shell"
            echo "  dotnet $(dotnet --version)"
            echo "  python $(python3 --version)"
            echo "  yosys $(yosys -V 2>&1)"
            echo ""
            echo "Commands:"
            echo "  yosys -s scripts/synth_counter.ys    # Verilog を NAND+NOT に合成"
            echo "  dotnet fsi src/RunTests.fsx          # F# 全テスト"
            echo "  python3 scripts/verify_cells.py      # セルライブラリ検証"
            echo "  web/run-test.sh                      # WebGPU ゴールデンテスト"
            echo "  wgpu-runner/target/release/wgpu-runner  # Rust WebGPU ランナー"
          '';
        };
      });
}
