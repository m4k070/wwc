module alu2 (
    input  [1:0] A,
    input  [1:0] B,
    input  [1:0] op,
    output [1:0] Y
);
    wire [1:0] sum  = A + B;
    wire [1:0] and_ = A & B;
    wire [1:0] or_  = A | B;
    wire [1:0] xor_ = A ^ B;

    assign Y = (op == 2'b00) ? sum  :
               (op == 2'b01) ? and_ :
               (op == 2'b10) ? or_  :
                                xor_;
endmodule
