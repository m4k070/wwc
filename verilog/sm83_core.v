module sm83_core (
    input  wire        clk,
    input  wire        rst,
    input  wire [7:0]  inst,       // 命令 (外部 ROM / テストベンチ)
    output wire [7:0]  pc_out,
    output wire [7:0]  a_out,
    output wire [7:0]  b_out,
    output wire [7:0]  c_out,
    output wire [3:0]  flags_out,
    output wire [7:0]  alu_out
);

    reg [7:0] pc;
    reg [7:0] a;
    reg [7:0] b;
    reg [7:0] c;
    reg [7:0] ir;
    reg [3:0] flags; // Z(3), N(2), H(1), C(0)

    wire [7:0] alu_result;
    wire [3:0] alu_flags;
    wire [7:0] pc_next;

    wire [2:0] opcode = ir[7:5];
    wire [1:0] alu_op = ir[4:3];
    wire [4:0] imm5   = ir[4:0];

    // ALU
    alu u_alu (
        .op1    (a),
        .op2    ({ir[7:5] == 3'b010 ? b : 8'd0}),
        .opcode (alu_op),
        .result (alu_result),
        .flags  (alu_flags)
    );

    // PC increment
    assign pc_next = pc + 8'd1;

    // ALU output observable
    assign alu_out = alu_result;

    // 出力ポート
    assign pc_out    = pc;
    assign a_out     = a;
    assign b_out     = b;
    assign c_out     = c;
    assign flags_out = flags;

    always @(posedge clk or posedge rst) begin
        if (rst) begin
            pc    <= 8'd0;
            a     <= 8'd0;
            b     <= 8'd0;
            c     <= 8'd0;
            ir    <= 8'd0;
            flags <= 4'd0;
        end else begin
            ir    <= inst;    // 命令フェッチ
            pc    <= pc_next; // PC 自動インクリメント

            case (opcode)
                3'b000: begin // LD A, #imm
                    // ir[7:5]=000, ir[4:0]=上位5bit → 下位3bitは0埋め
                    // 実際はテストで inst 全体を即値として使う
                end
                3'b001: begin // LD A, B (レジスタ間転送)
                    a <= b;
                end
                3'b010: begin // ADD A, B
                    a <= alu_result;
                    flags <= alu_flags;
                end
                3'b011: begin // SUB A, B
                    a <= alu_result;
                    flags <= alu_flags;
                end
                3'b100: begin // AND A, B
                    a <= alu_result;
                    flags <= alu_flags;
                end
                3'b101: begin // OR A, B
                    a <= alu_result;
                    flags <= alu_flags;
                end
                3'b110: begin // XOR A, B
                    a <= alu_result;
                    flags <= alu_flags;
                end
                3'b111: begin // CP A, B (フラグのみ更新)
                    flags <= alu_flags;
                end
                default: begin
                end
            endcase
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
    wire [7:0] or_res;
    wire [7:0] xor_res;

    assign add_res = op1 + op2;
    assign sub_res = op1 - op2;
    assign and_res = op1 & op2;
    assign or_res  = op1 | op2;
    assign xor_res = op1 ^ op2;

    wire [7:0] alu_mux =
        (opcode == 2'b00) ? add_res :
        (opcode == 2'b01) ? sub_res :
        (opcode == 2'b10) ? and_res :
                             xor_res;

    assign result = alu_mux;

    // Z flag
    wire z = (result == 8'd0);
    // N flag = 1 for sub, 0 for add/logic
    wire n = (opcode == 2'b01);
    // H flag (half-carry): bit 3 carry for add, bit 3 borrow for sub
    wire add_h = (op1[3:0] + op2[3:0]) >= 4'd8;
    wire sub_h = (op1[3:0] < op2[3:0]);
    wire h = (opcode == 2'b00) ? add_h :
             (opcode == 2'b01) ? sub_h : 1'b0;
    // C flag: carry for add, borrow for sub
    wire add_c = (op1 + op2) < op1;
    wire sub_c = (op1 < op2);
    wire c = (opcode == 2'b00) ? add_c :
             (opcode == 2'b01) ? sub_c : 1'b0;

    assign flags = {z, n, h, c};

endmodule
