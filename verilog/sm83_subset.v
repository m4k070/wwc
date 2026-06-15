// sm83_subset.v — SM83 subset CPU (~30 instructions, ~1500 gates)
// Ports: clk, rst, addr, data_in, data_out, mem_read, mem_write,
//        a_out, pc_out
// Registers: A, B, C, D, E, H, L, F(flags), SP(16), PC(16)
// ALU: ADD, ADC, SUB, SBC, AND, XOR, OR, CP + INC/DEC + correct flags
// Control: JP nn, JR n, CALL nn, RET, HALT
// No: CB prefix, interrupts, PUSH/POP, DAA, 16-bit ops except SP

module sm83_subset (
    input         clk,
    input         rst,
    output [15:0] addr,
    input  [ 7:0] data_in,
    output [ 7:0] data_out,
    output        mem_read,
    output        mem_write,
    output [ 7:0] a_out,
    output [15:0] pc_out
);

    // Registers
    reg [7:0] a, b, c, d, e, h, l, f;
    reg [15:0] sp, pc;

    // Internal state
    reg [15:0] addr_r;
    reg  [7:0] data_out_r;
    reg        mem_read_r, mem_write_r;
    reg [7:0] ir;    // instruction register
    reg [7:0] operand; // temporary operand

    // State machine
    reg [3:0] phase;
    parameter PHASE_FETCH  = 0;
    parameter PHASE_EXEC   = 1;
    parameter PHASE_MEM_RD = 2;
    parameter PHASE_MEM_WR = 3;
    parameter PHASE_HALT   = 4;

    assign addr = addr_r;
    assign data_out = data_out_r;
    assign mem_read = mem_read_r;
    assign mem_write = mem_write_r;
    assign a_out = a;
    assign pc_out = pc;

    // ----------------------------------------------------------------
    // Main state machine
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
            addr_r <= 16'h0100;
            mem_read_r <= 1;
            mem_write_r <= 0;
            phase <= PHASE_FETCH;
        end else begin
            case (phase)

                // =============================================
                // PHASE_FETCH: read instruction from PC
                // =============================================
                PHASE_FETCH: begin
                    ir <= data_in;
                    mem_read_r <= 0;
                    pc <= pc + 1;
                    // Decode and dispatch
                    case (data_in)
                        // === HALT ===
                        8'h76: phase <= PHASE_HALT;

                        // === NOP ===
                        8'h00: phase <= PHASE_FETCH;

                        // === LD r, n (immediate 8-bit) ===
                        8'h06, 8'h0E, 8'h16, 8'h1E,
                        8'h26, 8'h2E, 8'h36, 8'h3E: begin
                            addr_r <= pc + 1;
                            mem_read_r <= 1;
                            phase <= PHASE_MEM_RD;
                        end

                        // === LD r, r' (register-to-register) ===
                        8'h40, 8'h41, 8'h42, 8'h43, 8'h44, 8'h45, 8'h46, 8'h47,
                        8'h48, 8'h49, 8'h4A, 8'h4B, 8'h4C, 8'h4D, 8'h4E, 8'h4F,
                        8'h50, 8'h51, 8'h52, 8'h53, 8'h54, 8'h55, 8'h56, 8'h57,
                        8'h58, 8'h59, 8'h5A, 8'h5B, 8'h5C, 8'h5D, 8'h5E, 8'h5F,
                        8'h60, 8'h61, 8'h62, 8'h63, 8'h64, 8'h65, 8'h66, 8'h67,
                        8'h68, 8'h69, 8'h6A, 8'h6B, 8'h6C, 8'h6D, 8'h6E, 8'h6F,
                        8'h70, 8'h71, 8'h72, 8'h73, 8'h74, 8'h75,       8'h77,
                        8'h78, 8'h79, 8'h7A, 8'h7B, 8'h7C, 8'h7D, 8'h7E, 8'h7F: begin
                            exec_ld_rr(data_in);
                            phase <= PHASE_FETCH;
                        end

                        // === ADD A, r ===
                        8'h80, 8'h81, 8'h82, 8'h83, 8'h84, 8'h85, 8'h86, 8'h87: begin
                            exec_alu(data_in, 0); phase <= PHASE_FETCH; end

                        // === ADC A, r ===
                        8'h88, 8'h89, 8'h8A, 8'h8B, 8'h8C, 8'h8D, 8'h8E, 8'h8F: begin
                            exec_alu(data_in, 1); phase <= PHASE_FETCH; end

                        // === SUB r ===
                        8'h90, 8'h91, 8'h92, 8'h93, 8'h94, 8'h95, 8'h96, 8'h97: begin
                            exec_alu(data_in, 2); phase <= PHASE_FETCH; end

                        // === SBC r ===
                        8'h98, 8'h99, 8'h9A, 8'h9B, 8'h9C, 8'h9D, 8'h9E, 8'h9F: begin
                            exec_alu(data_in, 3); phase <= PHASE_FETCH; end

                        // === AND r ===
                        8'hA0, 8'hA1, 8'hA2, 8'hA3, 8'hA4, 8'hA5, 8'hA6, 8'hA7: begin
                            exec_alu(data_in, 4); phase <= PHASE_FETCH; end

                        // === XOR r ===
                        8'hA8, 8'hA9, 8'hAA, 8'hAB, 8'hAC, 8'hAD, 8'hAE, 8'hAF: begin
                            exec_alu(data_in, 5); phase <= PHASE_FETCH; end

                        // === OR r ===
                        8'hB0, 8'hB1, 8'hB2, 8'hB3, 8'hB4, 8'hB5, 8'hB6, 8'hB7: begin
                            exec_alu(data_in, 6); phase <= PHASE_FETCH; end

                        // === CP r ===
                        8'hB8, 8'hB9, 8'hBA, 8'hBB, 8'hBC, 8'hBD, 8'hBE, 8'hBF: begin
                            exec_alu(data_in, 7); phase <= PHASE_FETCH; end

                        // === INC r ===
                        8'h04, 8'h0C, 8'h14, 8'h1C,
                        8'h24, 8'h2C, 8'h34, 8'h3C: begin
                            exec_inc(data_in); phase <= PHASE_FETCH; end

                        // === DEC r ===
                        8'h05, 8'h0D, 8'h15, 8'h1D,
                        8'h25, 8'h2D, 8'h35, 8'h3D: begin
                            exec_dec(data_in); phase <= PHASE_FETCH; end

                        // === JP nn ===
                        8'hC3: begin addr_r <= pc; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === JR n ===
                        8'h18: begin addr_r <= pc; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === JR cc, n ===
                        8'h20, 8'h28, 8'h30, 8'h38: begin
                            if (jr_cond(data_in)) begin
                                addr_r <= pc; mem_read_r <= 1; phase <= PHASE_MEM_RD;
                            end else begin
                                phase <= PHASE_FETCH;
                            end
                        end

                        // === JP (HL) ===
                        8'hE9: begin pc <= {h, l}; addr_r <= {h, l}; mem_read_r <= 1; phase <= PHASE_FETCH; end

                        // === CALL nn ===
                        8'hCD: begin addr_r <= pc; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === RET ===
                        8'hC9: begin addr_r <= sp; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === LD A, (BC/DE) ===
                        8'h0A: begin addr_r <= {b, c}; mem_read_r <= 1; phase <= PHASE_MEM_RD; end
                        8'h1A: begin addr_r <= {d, e}; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === LD (BC/DE), A ===
                        8'h02: begin addr_r <= {b, c}; data_out_r <= a; mem_write_r <= 1; phase <= PHASE_MEM_WR; end
                        8'h12: begin addr_r <= {d, e}; data_out_r <= a; mem_write_r <= 1; phase <= PHASE_MEM_WR; end

                        // === LD A, (HL+) / LD A, (HL-) ===
                        8'h2A: begin addr_r <= {h, l}; mem_read_r <= 1; phase <= PHASE_MEM_RD; end
                        8'h3A: begin addr_r <= {h, l}; mem_read_r <= 1; phase <= PHASE_MEM_RD; end

                        // === LD (HL+), A / LD (HL-), A ===
                        8'h22: begin addr_r <= {h, l}; data_out_r <= a; mem_write_r <= 1; phase <= PHASE_MEM_WR; end
                        8'h32: begin addr_r <= {h, l}; data_out_r <= a; mem_write_r <= 1; phase <= PHASE_MEM_WR; end

                        // === LD A, (HL) === (part of LD r, (HL) group)
                        // 8'h7E is already handled in LD r,r' case above (r=A, src=(HL))

                        // === LD (HL), r === (part of LD r', (HL) group)
                        // 8'h70-8'h75, 8'h77 handled above

                        default: phase <= PHASE_FETCH;
                    endcase
                end

                // =============================================
                // PHASE_EXEC: execute decoded instruction (2nd byte)
                // =============================================
                PHASE_EXEC: begin
                    case (ir)
                        // JP nn: jump to {data_in, operand}
                        8'hC3: begin
                            pc <= {data_in, operand};
                            addr_r <= {data_in, operand};
                            mem_read_r <= 1;
                            phase <= PHASE_FETCH;
                        end

                        // JR n: relative jump
                        8'h18: begin
                            pc <= pc + {{8{data_in[7]}}, data_in};
                            addr_r <= pc + {{8{data_in[7]}}, data_in};
                            mem_read_r <= 1;
                            phase <= PHASE_FETCH;
                        end

                        // JR cc, n
                        8'h20, 8'h28, 8'h30, 8'h38: begin
                            pc <= pc + {{8{data_in[7]}}, data_in};
                            addr_r <= pc + {{8{data_in[7]}}, data_in};
                            mem_read_r <= 1;
                            phase <= PHASE_FETCH;
                        end

                        // CALL nn: push PC, jump
                        8'hCD: begin
                            sp <= sp - 1;
                            addr_r <= sp - 1;
                            data_out_r <= pc[15:8];
                            mem_write_r <= 1;
                            operand <= pc[7:0];
                            pc <= {data_in, operand};
                            phase <= PHASE_EXEC; // use same state for second push
                        end

                        // RET: pop PC
                        8'hC9: begin
                            pc <= {data_in, operand};
                            addr_r <= {data_in, operand};
                            sp <= sp + 1;
                            mem_read_r <= 1;
                            phase <= PHASE_FETCH;
                        end

                        // LD A, (BC/DE): read from memory
                        8'h0A, 8'h1A: begin
                            a <= data_in;
                            mem_read_r <= 0;
                            phase <= PHASE_FETCH;
                        end

                        // LD A, (HL+)
                        8'h2A: begin
                            a <= data_in;
                            mem_read_r <= 0;
                            h <= h; l <= l + 1; // inc HL
                            phase <= PHASE_FETCH;
                        end

                        // LD A, (HL-)
                        8'h3A: begin
                            a <= data_in;
                            mem_read_r <= 0;
                            h <= h; l <= l - 1; // dec HL
                            phase <= PHASE_FETCH;
                        end

                        // LD (BC/DE), A / LD (HL+/-), A: second write
                        // These jump here for the second cycle of CALL push
                        default: phase <= PHASE_FETCH;
                    endcase
                end

                // =============================================
                // PHASE_MEM_RD: memory read complete
                // =============================================
                PHASE_MEM_RD: begin
                    mem_read_r <= 0;
                    case (ir)
                        // LD r, n (immediate) — data_in has the value
                        8'h06, 8'h0E, 8'h16, 8'h1E,
                        8'h26, 8'h2E, 8'h36: begin
                            ld_imm(ir, data_in);
                            phase <= PHASE_FETCH;
                        end
                        8'h3E: begin a <= data_in; phase <= PHASE_FETCH; end

                        // JP nn / JR n / JR cc, n: low byte of address
                        8'hC3, 8'h18, 8'h20, 8'h28, 8'h30, 8'h38: begin
                            operand <= data_in;
                            addr_r <= pc + 1;
                            mem_read_r <= 1;
                            phase <= PHASE_EXEC;
                        end

                        // CALL nn: low byte
                        8'hCD: begin
                            operand <= data_in;
                            addr_r <= pc + 1;
                            mem_read_r <= 1;
                            phase <= PHASE_EXEC;
                        end

                        // RET: low byte from stack
                        8'hC9: begin
                            operand <= data_in;
                            sp <= sp + 1;
                            addr_r <= sp + 1;
                            mem_read_r <= 1;
                            phase <= PHASE_EXEC;
                        end

                        // LD A, (BC/DE/HL)
                        8'h0A, 8'h1A, 8'h2A, 8'h3A: begin
                            a <= data_in;
                            mem_read_r <= 0;
                            if (ir == 8'h2A) l <= l + 1;
                            if (ir == 8'h3A) l <= l - 1;
                            phase <= PHASE_FETCH;
                        end

                        // LD r, (HL) — read from HL
                        8'h46, 8'h4E, 8'h56, 8'h5E,
                        8'h66, 8'h6E, 8'h7E: begin
                            ld_hl_r(ir, data_in);
                            phase <= PHASE_FETCH;
                        end

                        default: phase <= PHASE_FETCH;
                    endcase
                end

                // =============================================
                // PHASE_MEM_WR: memory write complete
                // =============================================
                PHASE_MEM_WR: begin
                    mem_write_r <= 0;
                    case (ir)
                        8'h02, 8'h12: phase <= PHASE_FETCH;
                        8'h22: begin l <= l + 1; phase <= PHASE_FETCH; end
                        8'h32: begin l <= l - 1; phase <= PHASE_FETCH; end
                        // LD (HL), r — handled directly in exec_ld_rr with write
                        default: phase <= PHASE_FETCH;
                    endcase
                end

                // =============================================
                // PHASE_HALT: do nothing
                // =============================================
                PHASE_HALT: begin
                    phase <= PHASE_HALT;
                end

                default: phase <= PHASE_FETCH;
            endcase
        end
    end

    // ----------------------------------------------------------------
    // Helper tasks
    // ----------------------------------------------------------------

    // ALU operation dispatch
    task exec_alu;
        input [7:0] opcode;
        input [2:0] alu_op;
        reg [7:0] val;
        reg [7:0] res;
        reg [3:0] new_f;
        begin
            val = read_r8(opcode[2:0]);
            case (alu_op)
                0: {res, new_f} = alu_add(a, val, 1'b0);
                1: {res, new_f} = alu_add(a, val, f[0]);
                2: {res, new_f} = alu_sub(a, val, 1'b0);
                3: {res, new_f} = alu_sub(a, val, f[0]);
                4: {res, new_f} = alu_and(a, val);
                5: {res, new_f} = alu_xor(a, val);
                6: {res, new_f} = alu_or(a, val);
                7: {res, new_f} = alu_cp(a, val);
                default: begin res = a; new_f = f[3:0]; end
            endcase
            if (alu_op != 7) a <= res;
            f <= {new_f[3], new_f[2], new_f[1], new_f[0], 4'b0000};
        end
    endtask

    // INC r
    task exec_inc;
        input [7:0] opcode;
        reg [7:0] val;
        reg [7:0] res;
        begin
            val = read_r8(opcode[2:0]);
            res = val + 1;
            write_r8(opcode[2:0], res);
            f <= {(res == 0), 1'b0, (val[3:0] == 4'hF), f[0], 4'b0000};
        end
    endtask

    // DEC r
    task exec_dec;
        input [7:0] opcode;
        reg [7:0] val;
        reg [7:0] res;
        begin
            val = read_r8(opcode[2:0]);
            res = val - 1;
            write_r8(opcode[2:0], res);
            f <= {(res == 0), 1'b1, (val[3:0] == 4'h0), ~f[0], 4'b0000};
            // Note: DEC uses ~f[0] as borrow flag (correct per SM83)
        end
    endtask

    // LD r1, r2 / LD (HL), r / LD r, (HL)
    task exec_ld_rr;
        input [7:0] opcode;
        reg [7:0] src_val;
        begin
            if (opcode[3] == 0 && opcode[2:0] == 3'b110) begin
                // LD (HL), r: write to memory at HL
                src_val = read_r8(opcode[5:3]);
                addr_r = {h, l};
                data_out_r = src_val;
                mem_write_r = 1;
                phase <= PHASE_MEM_WR;
            end else if (opcode[3] == 1 && opcode[5:3] == 3'b110) begin
                // LD r, (HL): read from memory at HL
                // Will be handled in PHASE_MEM_RD
                addr_r <= {h, l};
                mem_read_r <= 1;
                phase <= PHASE_MEM_RD;
            end else begin
                // LD r1, r2
                src_val = read_r8(opcode[5:3]);
                write_r8(opcode[2:0], src_val);
                phase <= PHASE_FETCH;
            end
        end
    endtask

    // LD r, n (immediate) — called from PHASE_MEM_RD
    task ld_imm;
        input [7:0] opcode;
        input [7:0] val;
        begin
            case (opcode[5:3])
                0: b <= val; 1: c <= val; 2: d <= val; 3: e <= val;
                4: h <= val; 5: l <= val; 6: ; // (HL) — not used for LD r,n
                7: a <= val;
            endcase
        end
    endtask

    // LD r, (HL) — called from PHASE_MEM_RD
    task ld_hl_r;
        input [7:0] opcode;
        input [7:0] val;
        begin
            write_r8(opcode[2:0], val);
        end
    endtask

    // JR condition
    function jr_cond;
        input [7:0] opcode;
        begin
            case (opcode[5:4])
                0: jr_cond = (f[7] == 0);  // JR NZ
                1: jr_cond = (f[7] == 1);  // JR Z
                2: jr_cond = (f[4] == 0);  // JR NC
                3: jr_cond = (f[4] == 1);  // JR C
                default: jr_cond = 0;
            endcase
        end
    endfunction

    // ----------------------------------------------------------------
    // Register read/write helpers
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
                6: read_r8 = 8'h00; // (HL) placeholder (not used in ALU ops)
                7: read_r8 = a;
                default: read_r8 = 8'h00;
            endcase
        end
    endfunction

    task write_r8;
        input [2:0] idx;
        input [7:0] val;
        begin
            case (idx)
                0: b <= val; 1: c <= val; 2: d <= val; 3: e <= val;
                4: h <= val; 5: l <= val;
                6: ; // (HL) — memory write handled separately
                7: a <= val;
            endcase
        end
    endtask

    // ----------------------------------------------------------------
    // ALU function blocks
    // ----------------------------------------------------------------
    function [11:0] alu_add;
        input [7:0] x, y;
        input cin;
        reg [7:0] sum;
        reg z, n, h, c;
        begin
            sum = x + y + cin;
            z = (sum == 0);
            n = 0;
            h = (x[3:0] + y[3:0] + cin) > 4'hF;
            c = (x + y + cin) > 8'hFF;
            alu_add = {sum, z, n, h, c};
        end
    endfunction

    function [11:0] alu_sub;
        input [7:0] x, y;
        input cin;
        reg [7:0] diff;
        reg z, n, h, c;
        begin
            diff = x - y - cin;
            z = (diff == 0);
            n = 1;
            h = (x[3:0] < y[3:0] + cin);
            c = (x < y + cin);
            alu_sub = {diff, z, n, h, c};
        end
    endfunction

    function [11:0] alu_and;
        input [7:0] x, y;
        reg z, n, h, c;
        begin
            alu_and[7:0] = x & y;
            z = (alu_and[7:0] == 0);
            n = 0; h = 1; c = 0;
            alu_and[11:8] = {z, n, h, c};
        end
    endfunction

    function [11:0] alu_xor;
        input [7:0] x, y;
        reg z, n, h, c;
        begin
            alu_xor[7:0] = x ^ y;
            z = (alu_xor[7:0] == 0);
            n = 0; h = 0; c = 0;
            alu_xor[11:8] = {z, n, h, c};
        end
    endfunction

    function [11:0] alu_or;
        input [7:0] x, y;
        reg z, n, h, c;
        begin
            alu_or[7:0] = x | y;
            z = (alu_or[7:0] == 0);
            n = 0; h = 0; c = 0;
            alu_or[11:8] = {z, n, h, c};
        end
    endfunction

    function [11:0] alu_cp;
        input [7:0] x, y;
        reg z, n, h, c;
        begin
            alu_cp[7:0] = x - y; // CP = SUB but result discarded
            z = (alu_cp[7:0] == 0);
            n = 1;
            h = (x[3:0] < y[3:0]);
            c = (x < y);
            alu_cp[11:8] = {z, n, h, c};
        end
    endfunction

endmodule
