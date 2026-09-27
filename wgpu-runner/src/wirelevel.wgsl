// WireLevel WGSL — level-driven, pull-type directional wiring.
//   naga restriction: ptr<storage,...> cannot be a function parameter.
//   All storage access goes through globals b1 (read) / b2 (write).
//
// 1 dispatch = 1 世代。workgroup (16x16) = 1 タイル。各タイルは「この世代に変化したセルがあったか」を
// 集計し、変化があれば changeLog[slot] に 1 を足す。ホストは changeLog だけを読み戻して収束を判定する
// (changeLog[slot] == 0 ⇔ その世代で step(g) == g)。
const E: u32 = 0u; const W: u32 = 1u; const N: u32 = 2u; const S: u32 = 3u;
const K_EMPTY: u32 = 0u; const K_PIN: u32 = 1u; const K_WIRE: u32 = 2u;
const K_NAND: u32 = 3u; const K_CROSS: u32 = 4u; const K_DFF: u32 = 5u;

// タイルの一辺 (= workgroup_size)。gpu.rs の TILE_SIZE と一致させる
const TILE: u32 = 16u;

struct Dims { width: u32, height: u32, tiles_x: u32, tiles_y: u32, }
// 世代ごとのパラメータ (dynamic offset で 1 世代 1 エントリ)
struct GenParams { slot: u32, }

@group(0) @binding(0) var<storage, read>       b1        : array<u32>;
@group(0) @binding(1) var<storage, read_write> b2        : array<u32>;
@group(0) @binding(2) var<uniform>             dims      : Dims;
@group(0) @binding(3) var<uniform>             genParams : GenParams;
@group(0) @binding(4) var<storage, read_write> changeLog : array<atomic<u32>>;

fn readBuf(i: u32) -> u32 { return b1[i]; }
fn kind(c: u32) -> u32 { return (c >> 5u) & 7u; }
fn dir(c: u32) -> u32 { return (c >> 3u) & 3u; }
fn level(c: u32) -> u32 { return c & 1u; }

fn opposite(d: u32) -> u32 {
  switch (d) { case E { return W; } case W { return E; } case N { return S; } default { return N; } }
}
fn delta(d: u32) -> vec2<i32> {
  switch (d) { case E { return vec2( 1,  0); } case W { return vec2(-1,  0); }
               case N { return vec2( 0, -1); } default { return vec2( 0,  1); } }
}
fn presentedTo(c: u32, toward: u32) -> u32 {
  let k = kind(c);
  if (k == K_EMPTY) { return 0xFFFFFFFFu; }
  if (k == K_PIN || k == K_WIRE || k == K_NAND) { return level(c); }
  if (k == K_DFF) { return c & 1u; }
  if (k == K_CROSS) {
    let hDir = select(E, W, ((c >> 4u) & 1u) == 1u);
    let vDir = select(N, S, ((c >> 3u) & 1u) == 1u);
    if (toward == hDir) { return c & 1u; }
    if (toward == vDir) { return (c >> 1u) & 1u; }
    return 0xFFFFFFFFu;
  }
  return 0xFFFFFFFFu;
}
fn pullFrom(x: i32, y: i32, side: u32) -> u32 {
  let d = delta(side);
  let nx = x + d.x; let ny = y + d.y;
  if (nx < 0 || ny < 0 || nx >= i32(dims.width) || ny >= i32(dims.height)) { return 0xFFFFFFFFu; }
  return presentedTo(readBuf(u32(ny) * dims.width + u32(nx)), opposite(side));
}
fn stepCell(x: i32, y: i32) -> u32 {
  let i = u32(y) * dims.width + u32(x);
  let cell = readBuf(i);
  let k = kind(cell);
  if (k == K_EMPTY || k == K_PIN) { return cell; }
  if (k == K_WIRE) {
    return (cell & 0xF8u) | select(0u, 1u, pullFrom(x, y, opposite(dir(cell))) == 1u);
  }
  if (k == K_NAND) {
    let d = dir(cell);
    var allTrue: bool = true; var anyInput: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      if (s != d) {
        let v = pullFrom(x, y, s);
        if (v != 0xFFFFFFFFu) { anyInput = true; if (v == 0u) { allTrue = false; } }
      }
    }
    return (cell & 0xF8u) | select(0u, 1u, anyInput && !allTrue);
  }
  if (k == K_CROSS) {
    let hd = select(E, W, ((cell >> 4u) & 1u) == 1u);
    let vd = select(N, S, ((cell >> 3u) & 1u) == 1u);
    let hv = pullFrom(x, y, opposite(hd));
    let vv = pullFrom(x, y, opposite(vd));
    return (cell & 0xF8u) | (select(0u, 1u, vv == 1u) << 1u) | select(0u, 1u, hv == 1u);
  }
  if (k == K_DFF) {
    let d = dir(cell);
    let dVal = select(0u, 1u, pullFrom(x, y, opposite(d)) == 1u);
    var clk: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      let isPerp = ((d == E || d == W) && (s == N || s == S)) || ((d == N || d == S) && (s == E || s == W));
      if (isPerp) { if (pullFrom(x, y, s) == 1u) { clk = true; } }
    }
    let prevClk = (cell >> 1u) & 1u;
    let q = select(cell & 1u, dVal, clk && prevClk == 0u);
    return (cell & 0xF8u) | (select(0u, 1u, clk) << 1u) | q;
  }
  return cell;
}

// タイル内で 1 セルでも変化したら非 0 になる (workgroup 共有)
var<workgroup> tileChanged: atomic<u32>;

// タイル (tile_x, tile_y) の全セルを 1 世代進めて b2 に書き、タイル内に変化があったかを返す。
// 全スレッドが barrier に到達するよう、グリッド外のスレッドも return せずに通過させる。
fn stepTile(tile_x: u32, tile_y: u32, lid: vec3u, li: u32) -> bool {
  if (li == 0u) { atomicStore(&tileChanged, 0u); }
  workgroupBarrier();
  let x = tile_x * TILE + lid.x;
  let y = tile_y * TILE + lid.y;
  if (x < dims.width && y < dims.height) {
    let i = y * dims.width + x;
    let before = b1[i];
    let after = stepCell(i32(x), i32(y));
    b2[i] = after;
    if (after != before) { atomicStore(&tileChanged, 1u); }
  }
  workgroupBarrier();
  return atomicLoad(&tileChanged) != 0u;
}

// 全タイルを計算する (dispatch = tiles_x × tiles_y)
@compute @workgroup_size(16, 16)
fn step_dense(@builtin(workgroup_id) wg: vec3u,
              @builtin(local_invocation_id) lid: vec3u,
              @builtin(local_invocation_index) li: u32) {
  let changed = stepTile(wg.x, wg.y, lid, li);
  if (li == 0u && changed) { atomicAdd(&changeLog[genParams.slot], 1u); }
}
