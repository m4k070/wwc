/// Minimal 8-bit CPU for WireLevel CA demonstration.
/// SM83-like subset: NOP, LD A,imm, ADD A,imm, OUT, JMP.
/// All DFFs are simple $_DFF_P_ (posedge clk, no async reset/enable).
module mincpu(
    input clk,
    input rst,
    output reg [7:0] out
);
    reg [7:0] acc;
    reg [3:0] pc;
    reg [7:0] ir;
    reg state;

    // Program ROM (synthesizable function)
    // 0: LDI 1     acc = 1
    // 2: ADD 3     acc += 3 → 4
    // 4: OUT       port = acc (4)
    // 5: ADD 3     acc += 3 → 7
    // 7: OUT       port = acc (7)
    // 8: JMP 2     loop (acc: 4, 7, 10, 13, ...)
    function [7:0] rom;
        input [3:0] a;
        case (a)
            4'h0: rom = 8'h01;  // LDI
            4'h1: rom = 8'h01;
            4'h2: rom = 8'h02;  // ADD
            4'h3: rom = 8'h03;
            4'h4: rom = 8'h03;  // OUT
            4'h5: rom = 8'h02;  // ADD
            4'h6: rom = 8'h03;
            4'h7: rom = 8'h03;  // OUT
            4'h8: rom = 8'h04;  // JMP
            4'h9: rom = 8'h02;
            default: rom = 8'h00;
        endcase
    endfunction

    // Next-value wires (combinational, one big case)
    wire [7:0] n_ir;
    wire [3:0] n_pc;
    wire [7:0] n_acc;
    wire [7:0] n_out;
    wire n_state;

    localparam LDI = 8'h01;
    localparam ADD = 8'h02;
    localparam OUT = 8'h03;
    localparam JMP = 8'h04;

    // Combinational next-state logic
    assign n_state = (state == 0) ? 1'b1 : 1'b0;

    assign n_ir = (state == 0 && !rst) ? rom(pc) : ir;

    assign n_pc =
        rst          ? 4'h0 :
        (state == 0) ? pc + 1'b1 :
        (ir == JMP)  ? rom(pc) :
        (ir == LDI || ir == ADD) ? pc + 1'b1 :
        pc;

    assign n_acc =
        rst           ? 8'h0 :
        (state == 1 && ir == LDI) ? rom(pc) :
        (state == 1 && ir == ADD) ? acc + rom(pc) :
        acc;

    assign n_out =
        rst           ? 8'h0 :
        (state == 1 && ir == OUT) ? acc :
        out;

    // All DFFs are simple posedge-clk only (no async reset, no enable)
    always @(posedge clk) begin
        ir    <= n_ir;
        pc    <= n_pc;
        acc   <= n_acc;
        out   <= n_out;
        state <= n_state;
    end
endmodule
