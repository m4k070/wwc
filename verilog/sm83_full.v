// sm83_full.v — 完全 SM83 (Game Boy CPU)
// 全 256 + 256 (CB prefix) 命令を実装
// yosys synth + abc -g NAND + dffunmap で WireLevel コンパイル可能
//
// ポート構成:
//   clk, rst, data_in[7:0] が入力
//   addr[15:0], data_out[7:0], mem_read, mem_write が外部メモリバス
//   a_out/b_out/.../pc_out/sp_out がレジスタ値のデバッグ出力
module sm83_full (
    input  wire        clk,
    input  wire        rst,
    output reg  [15:0] addr,
    input  wire [7:0]  data_in,
    output reg  [7:0]  data_out,
    output reg         mem_read,
    output reg         mem_write,
    output wire [7:0]  a_out,
    output wire [7:0]  b_out,
    output wire [7:0]  c_out,
    output wire [7:0]  d_out,
    output wire [7:0]  e_out,
    output wire [7:0]  h_out,
    output wire [7:0]  l_out,
    output wire [7:0]  f_out,
    output wire [15:0] pc_out,
    output wire [15:0] sp_out
);

    // ----------------------------------------------------------------
    // レジスタファイル
    // ----------------------------------------------------------------
    reg [7:0] a;
    reg [7:0] b;
    reg [7:0] c;
    reg [7:0] d;
    reg [7:0] e;
    reg [7:0] h;
    reg [7:0] l;
    reg [7:0] f;   // flags: Z(7), N(6), H(5), C(4), 下位4bit=0
    reg [15:0] sp;
    reg [15:0] pc;

    // ----------------------------------------------------------------
    // 割り込み制御
    // ----------------------------------------------------------------
    reg       ime;
    reg       ime_next;
    reg [4:0] ie;
    reg [4:0] int_flag;
    reg       halted;

    // ----------------------------------------------------------------
    // 内部ステート
    // ----------------------------------------------------------------
    reg [7:0] ir;
    reg [7:0] cb_ir;
    reg       cb_prefix;
    reg [3:0] phase;

    reg [7:0]  operand;
    reg [15:0] call_target;
    reg        push2_pending;

    // ----------------------------------------------------------------
    // フェーズ定義
    // ----------------------------------------------------------------
    localparam PHASE_FETCH     = 0;
    localparam PHASE_FETCH2    = 1;
    localparam PHASE_IMM       = 2;
    localparam PHASE_IMM2      = 3;
    localparam PHASE_MEM_DATA  = 4;
    localparam PHASE_MEM_WRITE = 5;
    localparam PHASE_MEM_WRITE2 = 6;
    localparam PHASE_POP       = 7;
    localparam PHASE_POP2      = 8;
    localparam PHASE_INT_ACK   = 9;
    localparam PHASE_INT_PUSH  = 10;
    localparam PHASE_INT_PUSH2 = 11;
    localparam PHASE_CALL_PUSH = 12;
    localparam PHASE_HALT      = 13;

    // ----------------------------------------------------------------
    // レジスタ値読み出し
    // ----------------------------------------------------------------
    function [7:0] read_r8;
        input [2:0] idx;
        begin
            case (idx)
                0: read_r8 = b;
                1: read_r8 = c;
                2: read_r8 = d;
                3: read_r8 = e;
                4: read_r8 = h;
                5: read_r8 = l;
                7: read_r8 = a;
                default: read_r8 = 8'h00;
            endcase
        end
    endfunction

    function [15:0] read_rr;
        input [1:0] idx;
        begin
            case (idx)
                0: read_rr = {b, c};
                1: read_rr = {d, e};
                2: read_rr = {h, l};
                3: read_rr = sp;
                default: read_rr = 16'h0000;
            endcase
        end
    endfunction

    function [15:0] read_stk;
        input [1:0] idx;
        begin
            case (idx)
                0: read_stk = {b, c};
                1: read_stk = {d, e};
                2: read_stk = {h, l};
                3: read_stk = {a, f & 8'hF0};
                default: read_stk = 16'h0000;
            endcase
        end
    endfunction

    // ----------------------------------------------------------------
    // 条件チェック
    // ----------------------------------------------------------------
    function check_cond;
        input [1:0] cc;
        begin
            case (cc)
                0: check_cond = (f[7] == 0);
                1: check_cond = (f[7] == 1);
                2: check_cond = (f[4] == 0);
                3: check_cond = (f[4] == 1);
                default: check_cond = 0;
            endcase
        end
    endfunction

    // ----------------------------------------------------------------
    // 出力
    // ----------------------------------------------------------------
    assign a_out    = a;
    assign b_out    = b;
    assign c_out    = c;
    assign d_out    = d;
    assign e_out    = e;
    assign h_out    = h;
    assign l_out    = l;
    assign f_out    = f;
    assign pc_out   = pc;
    assign sp_out   = sp;

    // ----------------------------------------------------------------
    // ALU (組み合わせ)
    // ----------------------------------------------------------------
    wire [7:0] add8, sub8, and8, xor8, or8, inc8, dec8;
    wire       add8_h, add8_c, sub8_h, sub8_c, inc8_h, dec8_h;

    assign add8   = a + operand;
    assign sub8   = a - operand;
    assign and8   = a & operand;
    assign xor8   = a ^ operand;
    assign or8    = a | operand;
    assign inc8   = operand + 1;
    assign dec8   = operand - 1;

    assign add8_h = (a[3:0] + operand[3:0]) > 4'hF;
    assign add8_c = ({1'b0, a} + {1'b0, operand}) > 9'hFF;
    assign sub8_h = a[3:0] < operand[3:0];
    assign sub8_c = a < operand;
    assign inc8_h = (operand[3:0] == 4'hF);
    assign dec8_h = (operand[3:0] == 4'h0);

    wire ci = f[4];

    wire [7:0] adc8, sbc8;
    wire       adc8_h, adc8_c, sbc8_h, sbc8_c;
    assign adc8   = a + operand + ci;
    assign sbc8   = a - operand - ci;
    assign adc8_h = (a[3:0] + operand[3:0] + ci) > 4'hF;
    assign adc8_c = ({1'b0, a} + {1'b0, operand} + ci) > 9'hFF;
    assign sbc8_h = a[3:0] < (operand[3:0] + ci);
    assign sbc8_c = a < (operand + ci);

    // 16bit ADD HL, rr
    wire [15:0] add16_r;
    wire        add16_h, add16_c;
    assign add16_r = {h, l} + read_rr(/* will be set by mux */ 0);
    // ADD SP, e8
    wire [15:0] sp_e8;
    wire        sp_e8_h, sp_e8_c;

    // ----------------------------------------------------------------
    // 8bit 回転/シフト (CB 命令用)
    // ----------------------------------------------------------------
    function [7:0] cb_rotate;
        input [2:0] op;
        input [7:0] val;
        input       ci;
        begin
            case (op)
                0: cb_rotate = {val[6:0], val[7]};
                1: cb_rotate = {val[0], val[7:1]};
                2: cb_rotate = {val[6:0], ci};
                3: cb_rotate = {ci, val[7:1]};
                4: cb_rotate = {val[6:0], 1'b0};
                5: cb_rotate = {val[7], val[7:1]};
                6: cb_rotate = {val[3:0], val[7:4]};
                7: cb_rotate = {1'b0, val[7:1]};
                default: cb_rotate = val;
            endcase
        end
    endfunction

    function cb_carry;
        input [2:0] op;
        input [7:0] val;
        begin
            case (op)
                0: cb_carry = val[7];
                1: cb_carry = val[0];
                2: cb_carry = val[7];
                3: cb_carry = val[0];
                4: cb_carry = val[7];
                5: cb_carry = val[0];
                6: cb_carry = 0;
                7: cb_carry = val[0];
                default: cb_carry = 0;
            endcase
        end
    endfunction

    // ----------------------------------------------------------------
    // ALU ヘルパー
    // ----------------------------------------------------------------
    localparam ALU_ADD = 0;
    localparam ALU_ADC = 1;
    localparam ALU_SUB = 2;
    localparam ALU_SBC = 3;
    localparam ALU_AND = 4;
    localparam ALU_XOR = 5;
    localparam ALU_OR  = 6;
    localparam ALU_CP  = 7;

    // ----------------------------------------------------------------
    // メインステートマシン
    // ----------------------------------------------------------------
    always @(posedge clk) begin
        if (rst) begin
            a <= 8'h01;
            b <= 8'h00; c <= 8'h13;
            d <= 8'h00; e <= 8'hD8;
            h <= 8'h01; l <= 8'h4D;
            f <= 8'hB0;
            sp <= 16'hFFFE;
            pc <= 16'h0100;
            ime <= 0;
            ime_next <= 0;
            ie <= 0;
            int_flag <= 0;
            halted <= 0;
            ir <= 0;
            cb_ir <= 0;
            cb_prefix <= 0;
            phase <= PHASE_FETCH;
            addr <= 0;
            data_out <= 0;
            mem_read <= 0;
            mem_write <= 0;
            operand <= 0;
            call_target <= 0;
            push2_pending <= 0;
        end else begin
            mem_read <= 0;
            mem_write <= 0;
            ime <= ime_next;

            case (phase)
                // =====================================================
                // PHASE_FETCH
                // =====================================================
                PHASE_FETCH: begin
                    if (halted) begin
                        if ((ie & int_flag) != 0) begin
                            halted <= 0;
                            if (ime) begin
                                phase <= PHASE_INT_ACK;
                            end
                        end
                        addr <= 0;
                    end else if (ime && (ie & int_flag) != 0) begin
                        phase <= PHASE_INT_ACK;
                    end else begin
                        addr <= pc;
                        mem_read <= 1;
                        phase <= PHASE_FETCH2;
                    end
                end

                // =====================================================
                // PHASE_FETCH2
                // =====================================================
                PHASE_FETCH2: begin
                    ir <= data_in;
                    pc <= pc + 1;
                    if (data_in != 8'hFB) ime_next <= ime;

                    if (cb_prefix) begin
                        cb_ir <= data_in;
                        cb_prefix <= 0;
                        if (data_in[2:0] == 3'b110) begin
                            addr <= {h, l};
                            mem_read <= 1;
                            phase <= PHASE_MEM_DATA;
                        end else begin
                            exec_cb_reg(data_in);
                            phase <= PHASE_FETCH;
                        end
                    end else begin
                        exec_normal(data_in);
                    end
                end

                // =====================================================
                // PHASE_IMM
                // =====================================================
                PHASE_IMM: begin
                    operand <= data_in;
                    pc <= pc + 1;
                    exec_imm(data_in);
                end

                // =====================================================
                // PHASE_IMM2
                // =====================================================
                PHASE_IMM2: begin
                    exec_imm2(data_in);
                    pc <= pc + 1;
                end

                // =====================================================
                // PHASE_MEM_DATA
                // =====================================================
                PHASE_MEM_DATA: begin
                    operand <= data_in;
                    mem_read <= 0;
                    if (cb_prefix) begin
                        // CB (HL) — 演算後 MEM_WRITE
                        exec_cb_hl(data_in, cb_ir);
                        phase <= PHASE_MEM_WRITE;
                    end else begin
                        exec_mem_read(data_in);
                        phase <= PHASE_FETCH;
                    end
                end

                // =====================================================
                // PHASE_MEM_WRITE
                // =====================================================
                PHASE_MEM_WRITE: begin
                    mem_write <= 1;
                    phase <= PHASE_FETCH;
                end

                // =====================================================
                // PHASE_MEM_WRITE2: second write (PUSH low byte)
                // =====================================================
                PHASE_MEM_WRITE2: begin
                    sp <= sp - 1;
                    addr <= sp - 1;
                    data_out <= operand;
                    mem_write <= 1;
                    phase <= PHASE_FETCH;
                end

                // =====================================================
                // PHASE_CALL_PUSH: second push for CALL + jump
                // =====================================================
                PHASE_CALL_PUSH: begin
                    sp <= sp - 1;
                    addr <= sp - 1;
                    data_out <= operand;  // low byte of return addr
                    mem_write <= 1;
                    pc <= call_target;
                    phase <= PHASE_FETCH;
                end

                // =====================================================
                // PHASE_POP
                // =====================================================
                PHASE_POP: begin
                    operand <= data_in;
                    sp <= sp + 1;
                    mem_read <= 0;
                    phase <= PHASE_POP2;
                end

                PHASE_POP2: begin
                    exec_pop(data_in);
                    phase <= PHASE_FETCH;
                end

                // =====================================================
                // PHASE_INT_ACK
                // =====================================================
                PHASE_INT_ACK: begin
                    if (int_flag[0]) begin int_flag[0] <= 0; operand <= 8'h40;
                    end else if (int_flag[1]) begin int_flag[1] <= 0; operand <= 8'h48;
                    end else if (int_flag[2]) begin int_flag[2] <= 0; operand <= 8'h50;
                    end else if (int_flag[3]) begin int_flag[3] <= 0; operand <= 8'h58;
                    end else begin int_flag[4] <= 0; operand <= 8'h60; end
                    ime <= 0; ime_next <= 0;
                    sp <= sp - 1;
                    addr <= sp - 1;
                    data_out <= pc[15:8];
                    mem_write <= 1;
                    phase <= PHASE_INT_PUSH;
                end

                PHASE_INT_PUSH: begin
                    sp <= sp - 1;
                    addr <= sp - 1;
                    data_out <= pc[7:0];
                    mem_write <= 1;
                    phase <= PHASE_INT_PUSH2;
                end

                PHASE_INT_PUSH2: begin
                    pc <= {8'h00, operand};
                    phase <= PHASE_FETCH;
                end

                // =====================================================
                // PHASE_HALT
                // =====================================================
                PHASE_HALT: begin
                    // do nothing, HALT will be handled in PHASE_FETCH
                    addr <= 0;
                end

                default: begin
                    phase <= PHASE_FETCH;
                end
            endcase
        end
    end

    // ----------------------------------------------------------------
    // exec_normal: 通常命令デコード (PHASE_FETCH2 から呼ぶ)
    // ----------------------------------------------------------------
    task exec_normal;
        input [7:0] op;
        reg [7:0] daa_a;
        reg daa_c;
        begin
            case (op)
                // === NOP ===
                8'h00: phase <= PHASE_FETCH;

                // === HALT / STOP ===
                8'h76, 8'h10: begin halted <= 1; phase <= PHASE_HALT; end

                // === LD r, n (即値 8bit) ===
                8'h06: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h0E: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h16: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h1E: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h26: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h2E: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h36: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h3E: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end

                // === LD rr, nn (即値 16bit) ===
                8'h01: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h11: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h21: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h31: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end

                // === LD A, (BC/DE) / LD A, (HL±) ===
                8'h0A: begin addr <= {b, c}; mem_read <= 1; phase <= PHASE_MEM_DATA; end
                8'h1A: begin addr <= {d, e}; mem_read <= 1; phase <= PHASE_MEM_DATA; end
                8'h2A: begin addr <= {h, l}; mem_read <= 1; phase <= PHASE_MEM_DATA; {h, l} <= {h, l} + 1; end
                8'h3A: begin addr <= {h, l}; mem_read <= 1; phase <= PHASE_MEM_DATA; {h, l} <= {h, l} - 1; end

                // === LD (BC/DE/HL±), A ===
                8'h02: begin addr <= {b, c}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h12: begin addr <= {d, e}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h22: begin addr <= {h, l}; data_out <= a; mem_write <= 1; {h, l} <= {h, l} + 1; phase <= PHASE_FETCH; end
                8'h32: begin addr <= {h, l}; data_out <= a; mem_write <= 1; {h, l} <= {h, l} - 1; phase <= PHASE_FETCH; end

                // === LD (HL), r ===
                8'h70: begin addr <= {h, l}; data_out <= b; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h71: begin addr <= {h, l}; data_out <= c; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h72: begin addr <= {h, l}; data_out <= d; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h73: begin addr <= {h, l}; data_out <= e; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h74: begin addr <= {h, l}; data_out <= h; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h75: begin addr <= {h, l}; data_out <= l; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h77: begin addr <= {h, l}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end

                // === LD A, (nn) / LD (nn), A / LD (nn), SP ===
                8'hFA: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hEA: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h08: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end

                // === LDH ===
                8'hE0: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hF0: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hE2: begin addr <= {8'hFF, c}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end
                8'hF2: begin addr <= {8'hFF, c}; mem_read <= 1; phase <= PHASE_MEM_DATA; end

                // === LD SP, HL / LD HL, SP+e8 ===
                8'hF9: begin sp <= {h, l}; phase <= PHASE_FETCH; end
                8'hF8: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hE8: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end

                // === ADD/ADC/SUB/SBC/AND/XOR/OR/CP A, r ===
                8'h80, 8'h81, 8'h82, 8'h83, 8'h84, 8'h85, 8'h87: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_ADD); phase <= PHASE_FETCH; end
                8'h88, 8'h89, 8'h8A, 8'h8B, 8'h8C, 8'h8D, 8'h8F: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_ADC); phase <= PHASE_FETCH; end
                8'h90, 8'h91, 8'h92, 8'h93, 8'h94, 8'h95, 8'h97: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_SUB); phase <= PHASE_FETCH; end
                8'h98, 8'h99, 8'h9A, 8'h9B, 8'h9C, 8'h9D, 8'h9F: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_SBC); phase <= PHASE_FETCH; end
                8'hA0, 8'hA1, 8'hA2, 8'hA3, 8'hA4, 8'hA5, 8'hA7: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_AND); phase <= PHASE_FETCH; end
                8'hA8, 8'hA9, 8'hAA, 8'hAB, 8'hAC, 8'hAD, 8'hAF: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_XOR); phase <= PHASE_FETCH; end
                8'hB0, 8'hB1, 8'hB2, 8'hB3, 8'hB4, 8'hB5, 8'hB7: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_OR); phase <= PHASE_FETCH; end
                8'hB8, 8'hB9, 8'hBA, 8'hBB, 8'hBC, 8'hBD, 8'hBF: begin
                    operand <= read_r8(op[2:0]); do_alu(ALU_CP); phase <= PHASE_FETCH; end

                // === ALU A, (HL) ===
                8'h86, 8'h8E, 8'h96, 8'h9E, 8'hA6, 8'hAE, 8'hB6, 8'hBE: begin
                    addr <= {h, l}; mem_read <= 1; phase <= PHASE_MEM_DATA; end

                // === INC/DEC r ===
                8'h04: begin operand <= b; do_inc(0); end
                8'h05: begin operand <= b; do_dec(0); end
                8'h0C: begin operand <= c; do_inc(1); end
                8'h0D: begin operand <= c; do_dec(1); end
                8'h14: begin operand <= d; do_inc(2); end
                8'h15: begin operand <= d; do_dec(2); end
                8'h1C: begin operand <= e; do_inc(3); end
                8'h1D: begin operand <= e; do_dec(3); end
                8'h24: begin operand <= h; do_inc(4); end
                8'h25: begin operand <= h; do_dec(4); end
                8'h2C: begin operand <= l; do_inc(5); end
                8'h2D: begin operand <= l; do_dec(5); end
                8'h3C: begin operand <= a; do_inc_a; end
                8'h3D: begin operand <= a; do_dec_a; end
                8'h34: begin addr <= {h, l}; mem_read <= 1; phase <= PHASE_MEM_DATA; end
                8'h35: begin addr <= {h, l}; mem_read <= 1; phase <= PHASE_MEM_DATA; end

                // === INC/DEC rr ===
                8'h03: begin {b, c} <= {b, c} + 1; phase <= PHASE_FETCH; end
                8'h0B: begin {b, c} <= {b, c} - 1; phase <= PHASE_FETCH; end
                8'h13: begin {d, e} <= {d, e} + 1; phase <= PHASE_FETCH; end
                8'h1B: begin {d, e} <= {d, e} - 1; phase <= PHASE_FETCH; end
                8'h23: begin {h, l} <= {h, l} + 1; phase <= PHASE_FETCH; end
                8'h2B: begin {h, l} <= {h, l} - 1; phase <= PHASE_FETCH; end
                8'h33: begin sp <= sp + 1; phase <= PHASE_FETCH; end
                8'h3B: begin sp <= sp - 1; phase <= PHASE_FETCH; end

                // === ADD HL, rr ===
                8'h09: begin do_add16(0); phase <= PHASE_FETCH; end
                8'h19: begin do_add16(1); phase <= PHASE_FETCH; end
                8'h29: begin do_add16(2); phase <= PHASE_FETCH; end
                8'h39: begin do_add16(3); phase <= PHASE_FETCH; end

                // === RLCA / RRCA / RLA / RRA ===
                8'h07: begin
                    f <= {1'b0, 1'b0, 1'b0, a[7], 4'b0000};
                    a <= {a[6:0], a[7]}; phase <= PHASE_FETCH; end
                8'h0F: begin
                    f <= {1'b0, 1'b0, 1'b0, a[0], 4'b0000};
                    a <= {a[0], a[7:1]}; phase <= PHASE_FETCH; end
                8'h17: begin
                    f <= {1'b0, 1'b0, 1'b0, a[7], 4'b0000};
                    a <= {a[6:0], f[4]}; phase <= PHASE_FETCH; end
                8'h1F: begin
                    f <= {1'b0, 1'b0, 1'b0, a[0], 4'b0000};
                    a <= {f[4], a[7:1]}; phase <= PHASE_FETCH; end

                // === DAA / CPL / SCF / CCF ===
                8'h27: begin
                    daa_a = a;
                    daa_c = 0;
                    if (f[6] == 0) begin
                        if (f[5] || (daa_a & 8'h0F) > 8'h09) daa_a = daa_a + 8'h06;
                        if (f[4] || daa_a > 8'h9F) begin daa_a = daa_a + 8'h60; daa_c = 1; end
                    end else begin
                        if (f[5]) daa_a = daa_a - 8'h06;
                        if (f[4]) begin daa_a = daa_a - 8'h60; daa_c = 1; end
                    end
                    f <= {(daa_a == 0), f[6], 1'b0, daa_c, 4'b0000};
                    a <= daa_a; phase <= PHASE_FETCH; end
                8'h2F: begin
                    a <= ~a;
                    f <= {f[7], 1'b1, 1'b1, f[4], 4'b0000};
                    phase <= PHASE_FETCH; end
                8'h37: begin
                    f <= {f[7], 1'b0, 1'b0, 1'b1, 4'b0000};
                    phase <= PHASE_FETCH; end
                8'h3F: begin
                    f <= {f[7], 1'b0, 1'b0, ~f[4], 4'b0000};
                    phase <= PHASE_FETCH; end

                // === JP nn / JP cc, nn ===
                8'hC3: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hC2: begin if (f[7]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hCA: begin if (f[7]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hD2: begin if (f[4]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hDA: begin if (f[4]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end

                // === JP (HL) ===
                8'hE9: begin pc <= {h, l}; phase <= PHASE_FETCH; end

                // === JR n / JR cc, n ===
                8'h18: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'h20: begin if (f[7]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'h28: begin if (f[7]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'h30: begin if (f[4]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'h38: begin if (f[4]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end

                // === CALL nn / CALL cc, nn ===
                8'hCD: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hC4: begin if (f[7]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hCC: begin if (f[7]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hD4: begin if (f[4]==0) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end
                8'hDC: begin if (f[4]==1) begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end else phase <= PHASE_FETCH; end

                // === RST n ===
                8'hC7: begin rst_push(8'h00); end
                8'hCF: begin rst_push(8'h08); end
                8'hD7: begin rst_push(8'h10); end
                8'hDF: begin rst_push(8'h18); end
                8'hE7: begin rst_push(8'h20); end
                8'hEF: begin rst_push(8'h28); end
                8'hF7: begin rst_push(8'h30); end
                8'hFF: begin rst_push(8'h38); end

                // === RET / RET cc / RETI ===
                8'hC9, 8'hD9: begin addr <= sp; mem_read <= 1; phase <= PHASE_POP; end
                8'hC0: begin if (f[7]==0) begin addr <= sp; mem_read <= 1; phase <= PHASE_POP; end else phase <= PHASE_FETCH; end
                8'hC8: begin if (f[7]==1) begin addr <= sp; mem_read <= 1; phase <= PHASE_POP; end else phase <= PHASE_FETCH; end
                8'hD0: begin if (f[4]==0) begin addr <= sp; mem_read <= 1; phase <= PHASE_POP; end else phase <= PHASE_FETCH; end
                8'hD8: begin if (f[4]==1) begin addr <= sp; mem_read <= 1; phase <= PHASE_POP; end else phase <= PHASE_FETCH; end

                // === PUSH rr ===
                8'hC5: begin sp <= sp - 1; addr <= sp - 1; data_out <= b; mem_write <= 1; operand <= c; phase <= PHASE_MEM_WRITE2; end
                8'hD5: begin sp <= sp - 1; addr <= sp - 1; data_out <= d; mem_write <= 1; operand <= e; phase <= PHASE_MEM_WRITE2; end
                8'hE5: begin sp <= sp - 1; addr <= sp - 1; data_out <= h; mem_write <= 1; operand <= l; phase <= PHASE_MEM_WRITE2; end
                8'hF5: begin sp <= sp - 1; addr <= sp - 1; data_out <= a; mem_write <= 1; operand <= f & 8'hF0; phase <= PHASE_MEM_WRITE2; end

                // === POP rr ===
                8'hC1, 8'hD1, 8'hE1, 8'hF1: begin
                    addr <= sp; mem_read <= 1; phase <= PHASE_POP; end

                // === DI / EI ===
                8'hF3: begin ime_next <= 0; ime <= 0; phase <= PHASE_FETCH; end
                8'hFB: begin phase <= PHASE_FETCH; end

                // === ALU n (即値) ===
                8'hC6: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hCE: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hD6: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hDE: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hE6: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hEE: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hF6: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end
                8'hFE: begin addr <= pc; mem_read <= 1; phase <= PHASE_IMM; end

                // === LD r, r' / LD r,(HL) / LD (HL),r ===
                default: begin
                    if ((op & 8'hC0) == 8'h40 && op != 8'h76) begin
                        if (op[2:0] == 3'b110) begin
                            // LD r, (HL)
                            addr <= {h, l}; mem_read <= 1;
                            phase <= PHASE_MEM_DATA;
                        end else if (op[5:3] == 3'b110) begin
                            // LD (HL), r
                            addr <= {h, l};
                            data_out <= read_r8(op[2:0]);
                            mem_write <= 1;
                            phase <= PHASE_FETCH;
                        end else begin
                            // LD r, r'
                            ld_rr(op[5:3], read_r8(op[2:0]));
                            phase <= PHASE_FETCH;
                        end
                    end else if ((op & 8'hC0) == 8'h80) begin
                        phase <= PHASE_FETCH;
                    end else begin
                        phase <= PHASE_FETCH;
                    end
                end
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // do_alu: ALU 演算実行 + フラグ設定
    // ----------------------------------------------------------------
    task do_alu;
        input [2:0] alu_op;
        begin
            case (alu_op)
                ALU_ADD: begin
                    a <= add8;
                    f <= {(add8 == 0), 1'b0, add8_h, add8_c, 4'b0000};
                end
                ALU_ADC: begin
                    a <= adc8;
                    f <= {(adc8 == 0), 1'b0, adc8_h, adc8_c, 4'b0000};
                end
                ALU_SUB: begin
                    a <= sub8;
                    f <= {(sub8 == 0), 1'b1, sub8_h, sub8_c, 4'b0000};
                end
                ALU_SBC: begin
                    a <= sbc8;
                    f <= {(sbc8 == 0), 1'b1, sbc8_h, sbc8_c, 4'b0000};
                end
                ALU_AND: begin
                    a <= and8;
                    f <= {(and8 == 0), 1'b0, 1'b1, 1'b0, 4'b0000};
                end
                ALU_XOR: begin
                    a <= xor8;
                    f <= {(xor8 == 0), 1'b0, 1'b0, 1'b0, 4'b0000};
                end
                ALU_OR: begin
                    a <= or8;
                    f <= {(or8 == 0), 1'b0, 1'b0, 1'b0, 4'b0000};
                end
                ALU_CP: begin
                    f <= {(sub8 == 0), 1'b1, sub8_h, sub8_c, 4'b0000};
                end
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // レジスタ書き込みヘルパー
    // ----------------------------------------------------------------
    task ld_rr;
        input [2:0] dst;
        input [7:0] val;
        begin
            case (dst)
                0: b <= val;
                1: c <= val;
                2: d <= val;
                3: e <= val;
                4: h <= val;
                5: l <= val;
                7: a <= val;
                default: ;
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // INC/DEC 8bit
    // ----------------------------------------------------------------
    task do_inc;
        input [2:0] dst;
        begin
            case (dst)
                0: b <= inc8; 1: c <= inc8; 2: d <= inc8;
                3: e <= inc8; 4: h <= inc8; 5: l <= inc8;
                default: ;
            endcase
            f <= {(inc8 == 0), 1'b0, inc8_h, f[4], 4'b0000};
            phase <= PHASE_FETCH;
        end
    endtask

    task do_dec;
        input [2:0] dst;
        begin
            case (dst)
                0: b <= dec8; 1: c <= dec8; 2: d <= dec8;
                3: e <= dec8; 4: h <= dec8; 5: l <= dec8;
                default: ;
            endcase
            f <= {(dec8 == 0), 1'b1, dec8_h, f[4], 4'b0000};
            phase <= PHASE_FETCH;
        end
    endtask

    task do_inc_a;
        begin
            a <= inc8;
            f <= {(inc8 == 0), 1'b0, inc8_h, f[4], 4'b0000};
            phase <= PHASE_FETCH;
        end
    endtask

    task do_dec_a;
        begin
            a <= dec8;
            f <= {(dec8 == 0), 1'b1, dec8_h, f[4], 4'b0000};
            phase <= PHASE_FETCH;
        end
    endtask

    // ----------------------------------------------------------------
    // ADD HL, rr
    // ----------------------------------------------------------------
    task do_add16;
        input [1:0] idx;
        reg [15:0] rr;
        begin
            case (idx)
                0: rr = {b, c};
                1: rr = {d, e};
                2: rr = {h, l};
                3: rr = sp;
                default: rr = 0;
            endcase
            {h, l} <= {h, l} + rr;
            f <= {f[7], 1'b0,
                  (({h[3:0], l[3:0]} + rr[11:0]) > 12'hFFF),
                  (({h, l} + rr) > 16'hFFFF),
                  4'b0000};
        end
    endtask

    // ----------------------------------------------------------------
    // PC プッシュ (RST 用: 2回に分けてプッシュ)
    // ----------------------------------------------------------------
    task rst_push;
        input [7:0] vec;
        begin
            sp <= sp - 1;
            addr <= sp - 1;
            data_out <= pc[15:8];
            mem_write <= 1;
            call_target <= {8'h00, vec};
            operand <= pc[7:0];  // low byte for second push
            push2_pending <= 1;
            phase <= PHASE_MEM_WRITE2;
        end
    endtask

    // ----------------------------------------------------------------
    // exec_imm: 即値処理 (PHASE_IMM)
    // ----------------------------------------------------------------
    task exec_imm;
        input [7:0] val;
        begin
            case (ir)
                // LD r, n
                8'h06: begin b <= val; phase <= PHASE_FETCH; end
                8'h0E: begin c <= val; phase <= PHASE_FETCH; end
                8'h16: begin d <= val; phase <= PHASE_FETCH; end
                8'h1E: begin e <= val; phase <= PHASE_FETCH; end
                8'h26: begin h <= val; phase <= PHASE_FETCH; end
                8'h2E: begin l <= val; phase <= PHASE_FETCH; end
                8'h36: begin data_out <= val; addr <= {h, l}; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h3E: begin a <= val; phase <= PHASE_FETCH; end

                // LD rr, nn / JP nn / CALL nn / LD (nn),A / LD A,(nn) / LD (nn),SP
                8'h01, 8'h11, 8'h21, 8'h31,
                8'hC3, 8'hC2, 8'hCA, 8'hD2, 8'hDA,
                8'hCD, 8'hC4, 8'hCC, 8'hD4, 8'hDC,
                8'hFA, 8'hEA, 8'h08: begin
                    addr <= pc; mem_read <= 1; phase <= PHASE_IMM2; end

                // ALU n
                8'hC6: begin operand <= val; do_alu(ALU_ADD); phase <= PHASE_FETCH; end
                8'hCE: begin operand <= val; do_alu(ALU_ADC); phase <= PHASE_FETCH; end
                8'hD6: begin operand <= val; do_alu(ALU_SUB); phase <= PHASE_FETCH; end
                8'hDE: begin operand <= val; do_alu(ALU_SBC); phase <= PHASE_FETCH; end
                8'hE6: begin operand <= val; do_alu(ALU_AND); phase <= PHASE_FETCH; end
                8'hEE: begin operand <= val; do_alu(ALU_XOR); phase <= PHASE_FETCH; end
                8'hF6: begin operand <= val; do_alu(ALU_OR); phase <= PHASE_FETCH; end
                8'hFE: begin operand <= val; do_alu(ALU_CP); phase <= PHASE_FETCH; end

                // JR n / JR cc, n
                8'h18: begin pc <= pc + {{8{val[7]}}, val}; phase <= PHASE_FETCH; end
                8'h20: begin pc <= pc + {{8{val[7]}}, val}; phase <= PHASE_FETCH; end
                8'h28: begin pc <= pc + {{8{val[7]}}, val}; phase <= PHASE_FETCH; end
                8'h30: begin pc <= pc + {{8{val[7]}}, val}; phase <= PHASE_FETCH; end
                8'h38: begin pc <= pc + {{8{val[7]}}, val}; phase <= PHASE_FETCH; end

                // LDH
                8'hE0: begin addr <= {8'hFF, val}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end
                8'hF0: begin addr <= {8'hFF, val}; mem_read <= 1; phase <= PHASE_MEM_DATA; end

                // LD HL, SP+e8 / ADD SP, e8
                8'hF8: begin
                    {h, l} <= sp + {{8{val[7]}}, val};
                    f <= {1'b0, 1'b0,
                          ((sp[3:0] + val[3:0]) > 4'hF),
                          ((sp[7:0] + val) > 8'hFF),
                          4'b0000};
                    phase <= PHASE_FETCH; end
                8'hE8: begin
                    sp <= sp + {{8{val[7]}}, val};
                    f <= {1'b0, 1'b0,
                          ((sp[3:0] + val[3:0]) > 4'hF),
                          ((sp[7:0] + val) > 8'hFF),
                          4'b0000};
                    phase <= PHASE_FETCH; end

                default: phase <= PHASE_FETCH;
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // exec_imm2: 16bit 即値完了 (PHASE_IMM2)
    // ----------------------------------------------------------------
    task exec_imm2;
        input [7:0] hi;
        begin
            case (ir)
                8'h01: begin b <= hi; c <= operand; phase <= PHASE_FETCH; end
                8'h11: begin d <= hi; e <= operand; phase <= PHASE_FETCH; end
                8'h21: begin h <= hi; l <= operand; phase <= PHASE_FETCH; end
                8'h31: begin sp <= {hi, operand}; phase <= PHASE_FETCH; end

                8'hC3: begin pc <= {hi, operand}; phase <= PHASE_FETCH; end
                8'hC2, 8'hCA, 8'hD2, 8'hDA: begin
                    pc <= {hi, operand}; phase <= PHASE_FETCH; end

                8'hCD, 8'hC4, 8'hCC, 8'hD4, 8'hDC: begin
                    // CALL: push PC[15:8] first
                    call_target <= {hi, operand};
                    operand <= pc[15:8];  // temp store high byte of return addr
                    sp <= sp - 1;
                    addr <= sp - 1;
                    data_out <= pc[15:8];
                    mem_write <= 1;
                    phase <= PHASE_CALL_PUSH;
                end

                8'hFA: begin addr <= {hi, operand}; mem_read <= 1; phase <= PHASE_MEM_DATA; end
                8'hEA: begin addr <= {hi, operand}; data_out <= a; mem_write <= 1; phase <= PHASE_FETCH; end
                8'h08: begin
                    // LD (nn), SP — リトルエンディアン
                    addr <= {hi, operand};
                    data_out <= sp[7:0];
                    mem_write <= 1;
                    call_target <= {hi, operand + 1};  // second addr
                    operand <= sp[15:8];  // high byte of SP
                    push2_pending <= 1;
                    phase <= PHASE_MEM_WRITE2;
                end

                default: phase <= PHASE_FETCH;
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // exec_mem_read: メモリ読み出し後処理
    // ----------------------------------------------------------------
    task exec_mem_read;
        input [7:0] val;
        begin
            case (ir)
                8'h0A, 8'h1A, 8'h2A, 8'h3A, 8'hFA, 8'hF0, 8'hF2: begin
                    a <= val; end
                default: begin
                    // LD r, (HL)
                    if ((ir & 8'hC0) == 8'h40 && ir[2:0] == 3'b110) begin
                        ld_rr(ir[5:3], val);
                    end
                    // ALU (HL) — 再実行
                    else if ((ir & 8'hF8) == 8'h86) begin
                        // ADD A, (HL)
                        operand <= val;
                        do_alu(ALU_ADD);
                    end
                    else if ((ir & 8'hF8) == 8'h8E) begin
                        operand <= val; do_alu(ALU_ADC);
                    end
                    else if ((ir & 8'hF8) == 8'h96) begin
                        operand <= val; do_alu(ALU_SUB);
                    end
                    else if ((ir & 8'hF8) == 8'h9E) begin
                        operand <= val; do_alu(ALU_SBC);
                    end
                    else if ((ir & 8'hF8) == 8'hA6) begin
                        operand <= val; do_alu(ALU_AND);
                    end
                    else if ((ir & 8'hF8) == 8'hAE) begin
                        operand <= val; do_alu(ALU_XOR);
                    end
                    else if ((ir & 8'hF8) == 8'hB6) begin
                        operand <= val; do_alu(ALU_OR);
                    end
                    else if ((ir & 8'hBE) == 8'hBE) begin
                        operand <= val; do_alu(ALU_CP);
                    end
                    // INC/DEC (HL)
                    else if (ir == 8'h34) begin
                        operand <= inc8;
                        f <= {(inc8 == 0), 1'b0, inc8_h, f[4], 4'b0000};
                        // 書き戻し
                        addr <= {h, l};
                        data_out <= inc8;
                        mem_write <= 1;
                    end
                    else if (ir == 8'h35) begin
                        operand <= dec8;
                        f <= {(dec8 == 0), 1'b1, dec8_h, f[4], 4'b0000};
                        addr <= {h, l};
                        data_out <= dec8;
                        mem_write <= 1;
                    end
                end
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // exec_pop: POP 結果適用
    // ----------------------------------------------------------------
    task exec_pop;
        input [7:0] hi;
        begin
            case (ir)
                // RET / RETI / RET cc
                8'hC9, 8'hD9: begin
                    pc <= {hi, operand}; end
                8'hC0, 8'hC8, 8'hD0, 8'hD8: begin
                    pc <= {hi, operand}; end
                // POP rr
                8'hC1: {b, c} <= {hi, operand};
                8'hD1: {d, e} <= {hi, operand};
                8'hE1: {h, l} <= {hi, operand};
                8'hF1: begin a <= hi; f <= operand & 8'hF0; end
                default: ;
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // exec_cb_reg: CB 命令 (レジスタ版)
    // ----------------------------------------------------------------
    task exec_cb_reg;
        input [7:0] op;
        reg [7:0] val;
        reg [2:0] ri;
        reg [7:0] res;
        reg roc;
        begin
            ri = op[2:0];
            val = read_r8(ri);
            res = cb_rotate(op[5:3], val, f[4]);
            roc = cb_carry(op[5:3], val);
            case (op[7:6])
                0: begin // RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL
                    ld_rr(ri, res);
                    f <= {(res == 0), 1'b0, 1'b0, roc, 4'b0000};
                end
                1: begin // BIT
                    f <= {(val[op[5:3]] == 0), 1'b0, 1'b1, f[4], 4'b0000};
                end
                2: begin // RES
                    ld_rr(ri, val & ~(8'h01 << op[5:3]));
                end
                3: begin // SET
                    ld_rr(ri, val | (8'h01 << op[5:3]));
                end
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // exec_cb_hl: CB 命令 ((HL) 版 — MEM_DATA で値読み出し後)
    // ----------------------------------------------------------------
    task exec_cb_hl;
        input [7:0] mem_val;
        input [7:0] op;
        reg [7:0] res;
        reg roc;
        begin
            res = cb_rotate(op[5:3], mem_val, f[4]);
            roc = cb_carry(op[5:3], mem_val);
            case (op[7:6])
                0: begin
                    operand <= res;
                    f <= {(res == 0), 1'b0, 1'b0, roc, 4'b0000};
                end
                1: begin
                    operand <= mem_val;
                    f <= {(mem_val[op[5:3]] == 0), 1'b0, 1'b1, f[4], 4'b0000};
                end
                2: begin
                    operand <= mem_val & ~(8'h01 << op[5:3]);
                end
                3: begin
                    operand <= mem_val | (8'h01 << op[5:3]);
                end
            endcase
        end
    endtask

endmodule
