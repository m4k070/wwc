// membus_tiny.v — メモリバスを持つ最小の順序回路 (wgpu-runner --memory の 2 相クロック検証用)
// Ports: clk, rst, addr, data_in, data_out, mem_read, mem_write, a_out, pc_out
// (sm83_subset と同じポート名。runner の --memory が要求する最小集合)
//
// 動作 (2 周期で 1 組):
//   phase=0 (読出): addr = {8'h00, pc}, mem_read=1。posedge で a <= a ^ data_in, pc <= pc + 1
//   phase=1 (書込): addr = {8'hC0, pc}, mem_write=1, data_out=a (RAM 0xC000.. に a を書く)
// 同期リセット: pc=0, a=0, phase=0。
// マスター/スレーブの両方を通る経路 (pc インクリメンタ、a の XOR 累積、phase トグル) を持つ。
//
// 合成 → 2 相配線 → golden (routed/membus_tiny_2p*、wgpu-runner/memory-test.sh が使う):
//   nix develop --command yosys -p "read_verilog verilog/membus_tiny.v; synth -top membus_tiny -flatten; dffunmap; abc -g NAND; opt_clean; write_json verilog/membus_tiny.json"
//   dotnet fsi src/ExportRouted.fsx membus_tiny --place anneal --clocking two-phase --out <dir>
//     → <dir>/membus_tiny.{bin,meta.json} を routed/membus_tiny_2p.{bin,meta.json} にコピー
//   dotnet fsi src/ExportGolden.fsx routed/membus_tiny_2p_xor.json

module membus_tiny (
    input         clk,
    input         rst,
    output [15:0] addr,
    input  [ 7:0] data_in,
    output [ 7:0] data_out,
    output        mem_read,
    output        mem_write,
    output [ 7:0] a_out,
    output [ 7:0] pc_out
);
    reg [7:0] pc;
    reg [7:0] a;
    reg       phase;

    assign addr      = phase ? {8'hC0, pc} : {8'h00, pc};
    assign mem_read  = ~phase;
    assign mem_write = phase;
    assign data_out  = a;
    assign a_out     = a;
    assign pc_out    = pc;

    always @(posedge clk) begin
        if (rst) begin
            pc    <= 8'h00;
            a     <= 8'h00;
            phase <= 1'b0;
        end else if (phase == 1'b0) begin
            a     <= a ^ data_in;
            pc    <= pc + 8'h01;
            phase <= 1'b1;
        end else begin
            phase <= 1'b0;
        end
    end
endmodule
