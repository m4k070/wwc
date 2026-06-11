module top(input clk, output [3:0] q);
  reg [3:0] cnt = 4'b0000;
  always @(posedge clk) cnt <= cnt + 4'd1;
  assign q = cnt;
endmodule
