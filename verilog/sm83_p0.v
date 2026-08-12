module sm83_p0 (
    input  wire        clk,
    input  wire        rst,
    input  wire [7:0]  inst,
    input  wire [7:0]  data_in,
    output wire [7:0]  pc_out,
    output wire [7:0]  a_out,
    output wire [7:0]  b_out,
    output wire [7:0]  c_out,
    output wire [7:0]  d_out,
    output wire [3:0]  flags_out
);

    reg [7:0] pc;
    reg [7:0] a, b, c, d;
    reg [3:0] flags;

    function [7:0] read_reg;
        input [1:0] addr;
        begin
            case (addr)
                2'b00: read_reg = a;
                2'b01: read_reg = b;
                2'b10: read_reg = c;
                default: read_reg = d;
            endcase
        end
    endfunction

    wire [2:0] alu_opcode;
    wire [7:0] alu_op2;
    wire [7:0] alu_result;
    wire [3:0] alu_flags;
    wire [1:0] alu_r;

    assign alu_r = inst[1:0];
    assign alu_op2 = read_reg(alu_r);

    always @(*) begin
        case ({inst[7:5], inst[4:3]})
            5'b010_00: alu_opcode = 3'b000;
            5'b010_01: alu_opcode = 3'b001;
            5'b010_10: alu_opcode = 3'b010;
            5'b010_11: alu_opcode = 3'b011;
            5'b011_00: alu_opcode = 3'b100;
            5'b011_01: alu_opcode = 3'b101;
            default:   alu_opcode = 3'b000;
        endcase
    end

    alu_p0 u_alu (
        .op1    (a),
        .op2    (alu_op2),
        .opcode (alu_opcode),
        .result (alu_result),
        .flags  (alu_flags)
    );

    wire [1:0] incdec_r = inst[3:2];
    wire incdec_is_inc = (inst[4] == 1'b0);
    wire [7:0] incdec_val = read_reg(incdec_r);
    wire [7:0] inc_res = incdec_val + 8'd1;
    wire [7:0] dec_res = incdec_val - 8'd1;
    wire [7:0] incdec_result = incdec_is_inc ? inc_res : dec_res;
    wire [3:0] inc_flags = {inc_res == 8'd0, 1'b0, (incdec_val[3:0] + 4'd1) >= 4'd8, flags[0]};
    wire [3:0] dec_flags = {dec_res == 8'd0, 1'b1, incdec_val[3:0] < 4'd1, flags[0]};
    wire [3:0] incdec_flags = incdec_is_inc ? inc_flags : dec_flags;

    wire [7:0] pc_next = pc + 8'd1;

    always @(posedge clk or posedge rst) begin
        if (rst) begin
            pc    <= 8'd0;
            a     <= 8'd0;
            b     <= 8'd0;
            c     <= 8'd0;
            d     <= 8'd0;
            flags <= 4'd0;
        end else begin
            pc <= pc_next;

            case (inst[7:5])
                3'b000: begin
                    case (inst[4:3])
                        2'b00: a <= data_in;
                        2'b01: b <= data_in;
                        2'b10: c <= data_in;
                        2'b11: d <= data_in;
                    endcase
                end

                3'b001: begin
                    case (inst[4:3])
                        2'b00: a <= read_reg(inst[2:1]);
                        2'b01: b <= read_reg(inst[2:1]);
                        2'b10: c <= read_reg(inst[2:1]);
                        2'b11: d <= read_reg(inst[2:1]);
                    endcase
                end

                3'b010, 3'b011: begin
                    a <= alu_result;
                    flags <= alu_flags;
                end

                3'b100: begin
                    case (incdec_r)
                        2'b00: a <= incdec_result;
                        2'b01: b <= incdec_result;
                        2'b10: c <= incdec_result;
                        2'b11: d <= incdec_result;
                    endcase
                    flags <= incdec_flags;
                end

                default: begin
                end
            endcase
        end
    end

    assign pc_out = pc;
    assign a_out  = a;
    assign b_out  = b;
    assign c_out  = c;
    assign d_out  = d;
    assign flags_out = flags;

endmodule

module alu_p0 (
    input  wire [7:0] op1,
    input  wire [7:0] op2,
    input  wire [2:0] opcode,
    output wire [7:0] result,
    output wire [3:0] flags
);

    wire [7:0] add_res = op1 + op2;
    wire [7:0] sub_res = op1 - op2;
    wire [7:0] and_res = op1 & op2;
    wire [7:0] xor_res = op1 ^ op2;
    wire [7:0] or_res  = op1 | op2;

    wire [7:0] alu_res =
        (opcode == 3'b000) ? add_res :
        (opcode == 3'b001) ? sub_res :
        (opcode == 3'b010) ? and_res :
        (opcode == 3'b011) ? xor_res : or_res;

    wire [7:0] flag_res = (opcode == 3'b101) ? sub_res : alu_res;

    wire z = (flag_res == 8'd0);
    wire n = (opcode == 3'b001 || opcode == 3'b101);
    wire h = (opcode == 3'b000) ? ((op1[3:0] + op2[3:0]) >= 4'd8) :
             (opcode == 3'b001 || opcode == 3'b101) ? (op1[3:0] < op2[3:0]) :
             (opcode == 3'b010) ? 1'b1 : 1'b0;
    wire c = (opcode == 3'b000) ? ({1'b0, op1} + {1'b0, op2} > 8'hFF) :
             (opcode == 3'b001 || opcode == 3'b101) ? (op1 < op2) : 1'b0;

    assign result = (opcode == 3'b101) ? op1 : alu_res;
    assign flags = {z, n, h, c};

endmodule
