// alu8.v — SM83 ALU (combinational, 8 operations + correct flags)
// Yosys will normalize to NAND+NOT. No DFF — pure combinational.
// Inputs:  op[2:0] (000=ADD, 001=ADC, 010=SUB, 011=SBC,
//                  100=AND, 101=XOR, 110=OR,  111=CP)
//          a[7:0], b[7:0], flags_in[3:0]  — flags_in: {Z, N, H, C}
// Outputs: result[7:0], flags_out[3:0]    — flags_out: {Z, N, H, C}
//
// All flag computations match SM83 (LR35902) hardware behavior:
//   H (half-carry): bit 3 carry/borrow
//   C (carry):      bit 7 carry/borrow
//   N (subtract):   set for SUB/SBC/CP
//   Z (zero):       result == 0

module alu8 (
    input  [2:0] op,
    input  [7:0] a,
    input  [7:0] b,
    input  [3:0] flags_in,  // {Z, N, H, C}
    output [7:0] result,
    output [3:0] flags_out  // {Z, N, H, C}
);

    wire [7:0] add_res  = a + b;
    wire [7:0] adc_res  = a + b + flags_in[0];
    wire [7:0] sub_res  = a - b;
    wire [7:0] sbc_res  = a - b - flags_in[0];

    wire add_hc = (a[3:0] + b[3:0]) > 4'hF;
    wire add_c  = (a + b) > 8'hFF;
    wire adc_hc = (a[3:0] + b[3:0] + flags_in[0]) > 4'hF;
    wire adc_c  = (a + b + flags_in[0]) > 8'hFF;
    wire sub_hc = (a[3:0] < b[3:0]);
    wire sub_c  = (a < b);
    wire sbc_hc = (a[3:0] < b[3:0] + flags_in[0]);
    wire sbc_c  = (a < b + flags_in[0]);

    wire [7:0] and_res = a & b;
    wire [7:0] xor_res = a ^ b;
    wire [7:0] or_res  = a | b;
    wire [7:0] cp_res  = sub_res;  // CP = SUB but result discarded

    wire [7:0] alu_res =
        (op == 3'h0) ? add_res :
        (op == 3'h1) ? adc_res :
        (op == 3'h2) ? sub_res :
        (op == 3'h3) ? sbc_res :
        (op == 3'h4) ? and_res :
        (op == 3'h5) ? xor_res :
        (op == 3'h6) ? or_res  :
        /*op == 3'h7*/ cp_res;

    // Z flag: set when result (or A-B for CP) is zero
    wire z_flag = (alu_res == 8'h00);

    // N flag: set for subtract operations
    wire n_flag = (op == 3'h2) || (op == 3'h3) || (op == 3'h7);

    // H flag: half-carry
    wire h_flag =
        (op == 3'h0) ? add_hc :  // ADD
        (op == 3'h1) ? adc_hc :  // ADC
        (op == 3'h2) ? sub_hc :  // SUB
        (op == 3'h3) ? sbc_hc :  // SBC
        (op == 3'h4) ? 1'h1 :    // AND: always set H
        (op == 3'h5) ? 1'h0 :    // XOR: always clear H
        (op == 3'h6) ? 1'h0 :    // OR:  always clear H
                         sub_hc;  // CP: same as SUB

    // C flag: carry
    wire c_flag =
        (op == 3'h0) ? add_c :
        (op == 3'h1) ? adc_c :
        (op == 3'h2) ? sub_c :
        (op == 3'h3) ? sbc_c :
        (op == 3'h4) ? 1'h0 :    // AND: always clear C
        (op == 3'h5) ? 1'h0 :    // XOR: always clear C
        (op == 3'h6) ? 1'h0 :    // OR:  always clear C
                         sub_c;  // CP: same as SUB

    assign result = alu_res;
    assign flags_out = {z_flag, n_flag, h_flag, c_flag};

endmodule
