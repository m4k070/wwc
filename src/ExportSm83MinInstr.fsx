#r "bin/Debug/net8.0/WwHdl.dll"
// ExportSm83MinInstr.fsx — 命令レベル GPU 検証 (Phase 1b) 用エクスポート
//
// sm83_min をコンパイルし、wgpu-runner のプログラムモードが消費する 3 点を出力する:
//   web/sm83_min_instr_init.bin  — rst 適用済み・clk=0 settle 済みの初期グリッド
//   web/sm83_min_instr_meta.json — ピン/レジスタの正規化座標 (LSB first のバス表現)
//   web/sm83_min_program.json    — 命令列 + 期待レジスタ値 (Sm83MinModel で機械生成)
//
// F# の settle はリセットの 2 回のみ。以降のフェーズ実行は GPU に任せる。
// --verify: 先頭 2 命令を F# settle でも実行しモデルとクロスチェック (低速、既定 OFF)
open System.IO
open System.Text.Json
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

// ---------------------------------------------------------------------------
// Sm83MinModel — verilog/sm83_min.v の正確な写像 (実 SM83 のフラグ仕様ではない)
//   H (add): (op1[3:0] + op2[3:0]) >= 4'd8 — Verilog の幅規則で和は 4bit に
//            ラップされるため ((a&15)+(b&15)) & 15 >= 8 が正しい
//   H (sub): op1[3:0] < op2[3:0]
//   flags 更新は ALU op (opcode=10) のみ。LD/NOP は保持。
// ---------------------------------------------------------------------------
module Sm83MinModel =
    type St = { A: int; B: int; PC: int; F: int }
    let reset = { A = 0; B = 0; PC = 0; F = 0 }

    let exec (st: St) (inst: int) : St =
        let opcode = (inst >>> 6) &&& 3
        let imm6 = inst &&& 63
        let pc' = (st.PC + 1) &&& 0xFF
        match opcode with
        | 0b00 -> { st with A = imm6; PC = pc' }
        | 0b01 -> { st with B = imm6; PC = pc' }
        | 0b10 ->
            let aluOp = (inst >>> 4) &&& 3
            let a, b = st.A, st.B
            let raw, h, c =
                match aluOp with
                | 0 -> a + b, (((a &&& 15) + (b &&& 15)) &&& 15) >= 8, a + b > 0xFF
                | 1 -> a - b, (a &&& 15) < (b &&& 15), a < b
                | 2 -> a &&& b, false, false
                | _ -> a ^^^ b, false, false
            let r = raw &&& 0xFF
            let f =
                (if r = 0 then 8 else 0)
                ||| (if aluOp = 1 then 4 else 0)
                ||| (if h then 2 else 0)
                ||| (if c then 1 else 0)
            { st with A = r; F = f; PC = pc' }
        | _ -> { st with PC = pc' }

// --- アセンブラ ---
let ldA n = n &&& 63
let ldB n = 0x40 ||| (n &&& 63)
let ADD = 0x80
let SUB = 0x90
let AND = 0xA0
let XOR = 0xB0
let NOP = 0xC0

// 全 opcode + 全フラグ (Z/N/H/C) を踏む命令列。
// C(carry) は imm6<=63 のため 1 回の ADD では届かず、63+63 から ADD 連打で到達する。
let program : (string * int) list = [
    "NOP",       NOP
    "LD A,#42",  ldA 42
    "LD B,#17",  ldB 17
    "ADD A,B",   ADD      // a=59, H=1 (既知ケース)
    "SUB A,B",   SUB      // a=42, N=1
    "LD B,#42",  ldB 42
    "SUB A,B",   SUB      // a=0, Z=1
    "LD A,#5",   ldA 5
    "LD B,#9",   ldB 9
    "SUB A,B",   SUB      // a=252, borrow C=1
    "LD A,#63",  ldA 63
    "LD B,#63",  ldB 63
    "ADD A,B",   ADD      // 126
    "ADD A,B",   ADD      // 189
    "ADD A,B",   ADD      // 252
    "ADD A,B",   ADD      // 315 -> 59, carry C=1
    "AND A,B",   AND      // 59
    "XOR A,B",   XOR      // 4
    "LD B,#4",   ldB 4
    "XOR A,B",   XOR      // 0, Z=1
]

// --- モデルのセルフチェック (AGENTS.md の既知値 4 命令、F# settle 不要) ---
let selfCheck () =
    let expected = [
        NOP,      (0, 0, 1, 0x0)
        ldA 42,   (42, 0, 2, 0x0)
        ldB 17,   (42, 17, 3, 0x0)
        ADD,      (59, 17, 4, 0x2)
    ]
    let mutable st = Sm83MinModel.reset
    for (inst, (a, b, pc, f)) in expected do
        st <- Sm83MinModel.exec st inst
        if (st.A, st.B, st.PC, st.F) <> (a, b, pc, f) then
            failwithf "MODEL SELF-CHECK FAILED at inst=0x%02X: expected a=%d b=%d pc=%d f=0x%X, got a=%d b=%d pc=%d f=0x%X"
                inst a b pc f st.A st.B st.PC st.F
    printfn "Model self-check OK (known 4-instruction sequence)"

// ---------------------------------------------------------------------------
let exitCode =
    selfCheck ()

    let jsonPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "verilog", "sm83_min.json")
    let json = File.ReadAllText jsonPath
    let sw = System.Diagnostics.Stopwatch.StartNew()

    match compileWL json with
    | Error e ->
        eprintfn "COMPILE ERROR: %A" e
        1
    | Ok (grid, placed, pins) ->
        printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds
        let outDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "web"))

        // --- ポート構成 (sm83_min の NetId 割当は ExportSm83Multi.fsx と同一) ---
        let clkPin = pins.[NetId 2]
        let rstPin = pins.[NetId 3]
        let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]
        let gateCoord (netId: int) =
            placed |> List.find (fun p -> p.Gate.Output = NetId netId) |> fun p -> p.Coord
        let pcOut  = [| for i in 0..7 -> gateCoord (12 + i) |]
        let aOut   = [| for i in 0..7 -> gateCoord (20 + i) |]
        let bOut   = [| for i in 0..5 -> gateCoord (28 + i) |]   // b は 6bit (上位は yosys が除去)
        let flOut  = [| for i in 0..3 -> gateCoord (34 + i) |]

        // --- 正規化 (exportGrid と同一の min 計算。.bin と同じ座標系にする) ---
        let coords = grid |> Map.toList |> List.map fst
        let minX = coords |> List.map (fun c -> c.X) |> List.min
        let minY = coords |> List.map (fun c -> c.Y) |> List.min
        let width  = (coords |> List.map (fun c -> c.X) |> List.max) - minX + 1
        let height = (coords |> List.map (fun c -> c.Y) |> List.max) - minY + 1
        let norm (c: Coord) = {| x = c.X - minX; y = c.Y - minY |}

        let meta = {|
            circuit = "sm83_min"
            width = width
            height = height
            pins = {|
                clk  = [| norm clkPin |]
                rst  = [| norm rstPin |]
                inst = [| for p in instPins -> norm p |]
            |}
            regs = {|
                pc    = [| for c in pcOut -> norm c |]
                a     = [| for c in aOut -> norm c |]
                b     = [| for c in bOut -> norm c |]
                flags = [| for c in flOut -> norm c |]
            |}
        |}
        let jsonOpts = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(Path.Combine(outDir, "sm83_min_instr_meta.json"), JsonSerializer.Serialize(meta, jsonOpts))
        printfn "Exported sm83_min_instr_meta.json (%dx%d)" width height

        // --- init.bin: rst=1 で settle → rst=0 で settle (clk=0 のまま) ---
        let settlePhase label limit g =
            let sw2 = System.Diagnostics.Stopwatch.StartNew()
            let g', t = settle limit g
            printfn "  settle %s: %d gen (%.1fs)" label t sw2.Elapsed.TotalSeconds
            g'
        let afterRst = grid |> setPin rstPin true |> setPin clkPin false |> settlePhase "rst=1" 2500
        let initGrid = afterRst |> setPin rstPin false |> settlePhase "rst=0" 2500
        File.WriteAllBytes(Path.Combine(outDir, "sm83_min_instr_init.bin"), exportGrid initGrid)
        printfn "Exported sm83_min_instr_init.bin"

        // --- program JSON: モデルで期待値を機械生成 ---
        let steps =
            program
            |> List.mapFold
                (fun st (desc, inst) ->
                    let st' = Sm83MinModel.exec st inst
                    {| desc = desc
                       pins = {| inst = inst |}
                       expect = {| pc = st'.PC; a = st'.A; b = st'.B; flags = st'.F |} |}, st')
                Sm83MinModel.reset
            |> fst
        let progJson = {|
            circuit = "sm83_min"
            meta = "sm83_min_instr_meta.json"
            init = "sm83_min_instr_init.bin"
            // 実測の最長フェーズは high 3598 gen (LD B 系)。余裕を見て 6000。
            maxStepsPerPhase = 6000
            checkInterval = 256
            steps = steps
        |}
        File.WriteAllText(Path.Combine(outDir, "sm83_min_program.json"), JsonSerializer.Serialize(progJson, jsonOpts))
        printfn "Exported sm83_min_program.json (%d steps)" steps.Length

        // --- --verify: 先頭 2 命令を F# settle でも実行しモデルとクロスチェック ---
        if fsi.CommandLineArgs |> Array.contains "--verify" then
            printfn "\n=== VERIFY (F# settle vs model, first 2 instructions) ==="
            let setInst (v: int) (g: LGrid) =
                let mutable g = g
                for i in 0..7 do g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
                g
            let readReg (cs: Coord[]) (g: LGrid) =
                cs |> Array.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> Array.sum
            let mutable g = initGrid
            let mutable st = Sm83MinModel.reset
            let mutable ok = true
            for (desc, inst) in program |> List.truncate 2 do
                g <- g |> setInst inst |> setPin clkPin false |> settlePhase "setup" 2500
                g <- g |> setPin clkPin true |> settlePhase "high" 2500
                st <- Sm83MinModel.exec st inst
                let a, b = readReg aOut g, readReg bOut g
                let pc, f = readReg pcOut g, readReg flOut g
                let m = (st.A, st.B, st.PC, st.F)
                printfn "  %s: grid a=%d b=%d pc=%d flags=0x%X / model a=%d b=%d pc=%d flags=0x%X %s"
                    desc a b pc f st.A st.B st.PC st.F (if (a, b, pc, f) = m then "✓" else "*** MISMATCH ***")
                if (a, b, pc, f) <> m then ok <- false
            if not ok then failwith "VERIFY FAILED: F# settle disagrees with model"
            printfn "Verify OK"

        printfn "\nTotal: %.1fs" sw.Elapsed.TotalSeconds
        0

exit exitCode
