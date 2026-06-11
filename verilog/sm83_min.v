// SM83 サブセット — 最小 CPU
// 命令エンコーディング (8bit):
//   [7:6] opcode
//     00: LD A, #imm      bits[5:0] → A
//     01: LD B, #imm      bits[5:0] → B
//     10: ALU A, B        bits[5:4] = ALU op (00=ADD 01=SUB 10=AND 11=XOR)
//     11: NOP
module sm83_min (
    input  wire        clk,
    input  wire        rst,
    input  wire [7:0]  inst,          // 命令
    output wire [7:0]  pc_out,        // プログラムカウンタ
    output wire [7:0]  a_out,         // アキュムレータ A
    output wire [7:0]  b_out,         // レジスタ B
    output wire [3:0]  flags_out      // フラグ Z,N,H,C
);

    reg [7:0] pc;
    reg [7:0] a;
    reg [7:0] b;
    reg [3:0] flags;  // Z(3), N(2), H(1), C(0)

    wire [1:0] opcode = inst[7:6];
    wire [1:0] alu_op = inst[5:4];
    wire [5:0] imm6   = inst[5:0];

    wire [7:0] alu_result;
    wire [3:0] alu_flags;

    wire [7:0] a_next;
    wire [7:0] b_next;

    alu u_alu (
        .op1    (a),
        .op2    (b),
        .opcode (alu_op),
        .result (alu_result),
        .flags  (alu_flags)
    );

    assign pc_out = pc;
    assign a_out  = a;
    assign b_out  = b;
    assign flags_out = flags;

    // PC は常に +1
    wire [7:0] pc_inc = pc + 8'd1;
    wire [7:0] pc_next = pc_inc;

    // LD A, #imm: 即値を A にロード (imm6 をゼロ拡張)
    wire [7:0] imm_a = {2'b00, inst[5:0]};
    // LD B, #imm: 即値を B にロード
    wire [7:0] imm_b = {2'b00, inst[5:0]};

    assign a_next = (opcode == 2'b00) ? imm_a :
                    (opcode == 2'b10) ? alu_result : a;

    assign b_next = (opcode == 2'b01) ? imm_b : b;

    always @(posedge clk or posedge rst) begin
        if (rst) begin
            pc    <= 8'd0;
            a     <= 8'd0;
            b     <= 8'd0;
            flags <= 4'd0;
        end else begin
            pc    <= pc_next;
            a     <= a_next;
            b     <= b_next;
            flags <= (opcode == 2'b10) ? alu_flags : flags;
        end
    end

endmodule

module alu (
    input  wire [7:0] op1,
    input  wire [7:0] op2,
    input  wire [1:0] opcode,
    output wire [7:0] result,
    output wire [3:0] flags
);

    wire [7:0] add_res;
    wire [7:0] sub_res;
    wire [7:0] and_res;
    wire [7:0] xor_res;

    assign add_res = op1 + op2;
    assign sub_res = op1 - op2;
    assign and_res = op1 & op2;
    assign xor_res = op1 ^ op2;

    assign result = (opcode == 2'b00) ? add_res :
                    (opcode == 2'b01) ? sub_res :
                    (opcode == 2'b10) ? and_res : xor_res;

    wire z = (result == 8'd0);
    wire n = (opcode == 2'b01);
    wire h = (opcode == 2'b00) ? ((op1[3:0] + op2[3:0]) >= 4'd8) :
             (opcode == 2'b01) ? (op1[3:0] < op2[3:0]) : 1'b0;
    wire c = (opcode == 2'b00) ? ({1'b0, op1} + {1'b0, op2} > 8'hFF) :
             (opcode == 2'b01) ? (op1 < op2) : 1'b0;

    assign flags = {z, n, h, c};

endmodule
