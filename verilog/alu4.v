module alu4 (
    input  [3:0] A,
    input  [3:0] B,
    input  [1:0] op,
    output [3:0] Y
);
    wire [3:0] sum  = A + B;
    wire [3:0] and_ = A & B;
    wire [3:0] or_  = A | B;
    wire [3:0] xor_ = A ^ B;

    assign Y = (op == 2'b00) ? sum  :
               (op == 2'b01) ? and_ :
               (op == 2'b10) ? or_  :
                                xor_;
endmodule
