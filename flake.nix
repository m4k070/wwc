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
      in {
        devShells.default = pkgs.mkShell {
          name = "wwc";
          packages = with pkgs; [
            # F# / .NET
            dotnet-sdk_8

            # WireWorld シミュレーター (GUI確認用)
            # bgolly は多状態CA未サポートのため自動検証は scripts/verify_cells.py を使う
            golly

            # Python (セルライブラリ検証スクリプト)
            python3

            # Verilog 合成 (HDL → NAND+NOT)
            yosys

            # ユーティリティ
            git
          ];

          shellHook = ''
            echo "wwc dev shell"
            echo "  dotnet $(dotnet --version)"
            echo "  python $(python3 --version)"
            echo "  yosys $(yosys -V 2>&1)"
            echo ""
            echo "Commands:"
            echo "  yosys -s scripts/synth_counter.ys    # Verilog を NAND+NOT に合成"
            echo "  dotnet fsi src/RunTests.fsx          # F# 全テスト"
            echo "  dotnet fsi src/ExportRLE.fsx         # Golly RLE 生成 → golly/"
            echo "  python3 scripts/verify_cells.py      # セルライブラリ検証"
            echo "  golly golly/cell_junc3.rle           # Golly GUI で確認"
          '';
        };
      });
}
