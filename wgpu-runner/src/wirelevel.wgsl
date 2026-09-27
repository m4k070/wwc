// WireLevel WGSL — level-driven, pull-type directional wiring.
//   naga restriction: ptr<storage,...> cannot be a function parameter.
//   All storage access goes through globals b1 (read) / b2 (write).
//
// 1 dispatch = 1 世代。workgroup (16x16) = 1 タイル。各タイルは「この世代に変化したセルがあったか」を
// 集計し、変化があれば changeLog[slot] に 1 を足す。ホストは changeLog だけを読み戻して収束を判定する
// (changeLog[slot] == 0 ⇔ その世代で step(g) == g)。
//
// エントリポイント:
//   step_dense  全タイルを計算する (参照実装。--engine dense)
//   step_full   全タイルを計算し、変化したタイルと隣接タイルを次世代のアクティブリストに積む
//               (初回と、ホストがピンを書き換えた直後の 1 世代)
//   step_list   アクティブリストのタイルだけを計算する (dispatch_workgroups_indirect)
//
// 計算しないタイルの正しさ (ping-pong の不変条件):
//   世代 t で計算しないタイル T は「世代 t-1 に T 自身も 4 近傍タイルの T 側の辺も変化しなかった」タイル。
//   von Neumann 近傍 1 セルなので step(g_t) の T 部分は g_t の T 部分と同じ。さらに両バッファの T 部分は
//   等しい (T を最後に計算した世代で変化 0 だった or 以後一度も書いていない) ので、書かなくてよい。
const E: u32 = 0u; const W: u32 = 1u; const N: u32 = 2u; const S: u32 = 3u;
const K_EMPTY: u32 = 0u; const K_PIN: u32 = 1u; const K_WIRE: u32 = 2u;
const K_NAND: u32 = 3u; const K_CROSS: u32 = 4u; const K_DFF: u32 = 5u;

// タイルの一辺 (= workgroup_size)。gpu.rs の TILE_SIZE と一致させる
const TILE: u32 = 16u;

// stamp_base: このバッチの世代 slot のスタンプは stamp_base + slot + 1 (単調増加。stamps の比較に使う)
struct Dims { width: u32, height: u32, tiles_x: u32, tiles_y: u32, stamp_base: u32, }
// 世代ごとのパラメータ (dynamic offset で 1 世代 1 エントリ)
struct GenParams { slot: u32, }
// dispatch_workgroups_indirect の引数。x = 1 (制御用 workgroup) + アクティブタイル数
struct DispatchArgs { x: atomic<u32>, y: u32, z: u32, }

// タイルの変化マスク。EDGE_* は「その辺のセルが変化した」= 隣のタイルが次世代に影響を受ける
const CHANGED_ANY: u32 = 1u;
const CHANGED_WEST_EDGE: u32 = 2u;
const CHANGED_EAST_EDGE: u32 = 4u;
const CHANGED_NORTH_EDGE: u32 = 8u;
const CHANGED_SOUTH_EDGE: u32 = 16u;
// アクティブリストの args.x の初期値 (制御用 workgroup の 1 個)
const ARGS_CONTROL_WORKGROUPS: u32 = 1u;

@group(0) @binding(0) var<storage, read>       b1        : array<u32>;
@group(0) @binding(1) var<storage, read_write> b2        : array<u32>;
@group(0) @binding(2) var<uniform>             dims      : Dims;
@group(0) @binding(3) var<uniform>             genParams : GenParams;
@group(0) @binding(4) var<storage, read_write> changeLog : array<atomic<u32>>;

// アクティブリスト (3 本を世代ごとに回す: cur = 今世代の入力、next = 次世代分を積む、free = 次々世代用に空にする)
@group(1) @binding(0) var<storage, read>       listCur   : array<u32>;
@group(1) @binding(1) var<storage, read_write> listNext  : array<u32>;
@group(1) @binding(2) var<storage, read_write> argsNext  : DispatchArgs;
@group(1) @binding(3) var<storage, read_write> argsFree  : DispatchArgs;
// タイルごとに「最後に積まれた世代のスタンプ」。同じ世代に同じタイルを 2 度積まないため
@group(1) @binding(4) var<storage, read_write> stamps    : array<atomic<u32>>;

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

// タイル内の変化マスク (CHANGED_*) の集計 (workgroup 共有)
var<workgroup> tileChange: atomic<u32>;

// タイル内の位置 lid のセルが変化したときに立てるマスク
fn changeMask(lid: vec3u) -> u32 {
  var m = CHANGED_ANY;
  if (lid.x == 0u)        { m = m | CHANGED_WEST_EDGE; }
  if (lid.x == TILE - 1u) { m = m | CHANGED_EAST_EDGE; }
  if (lid.y == 0u)        { m = m | CHANGED_NORTH_EDGE; }
  if (lid.y == TILE - 1u) { m = m | CHANGED_SOUTH_EDGE; }
  return m;
}

// タイル (tile_x, tile_y) の全セルを 1 世代進めて b2 に書き、タイルの変化マスクを返す (0 = 変化なし)。
// 全スレッドが barrier に到達するよう、グリッド外のスレッドも return せずに通過させる。
fn stepTile(tile_x: u32, tile_y: u32, lid: vec3u, li: u32) -> u32 {
  if (li == 0u) { atomicStore(&tileChange, 0u); }
  workgroupBarrier();
  let x = tile_x * TILE + lid.x;
  let y = tile_y * TILE + lid.y;
  if (x < dims.width && y < dims.height) {
    let i = y * dims.width + x;
    let before = b1[i];
    let after = stepCell(i32(x), i32(y));
    b2[i] = after;
    if (after != before) { atomicOr(&tileChange, changeMask(lid)); }
  }
  workgroupBarrier();
  return atomicLoad(&tileChange);
}

// タイル t を次世代のアクティブリストに積む (この世代で既に積まれていれば何もしない)
fn activate(t: u32) {
  let stamp = dims.stamp_base + genParams.slot + 1u;
  let prev = atomicMax(&stamps[t], stamp);
  if (prev < stamp) {
    let idx = atomicAdd(&argsNext.x, 1u);
    listNext[idx - ARGS_CONTROL_WORKGROUPS] = t;
  }
}

// 変化したタイルを記録し、自身と「変化した辺の向こう」の隣接タイルを次世代に積む
fn publishChange(tile_x: u32, tile_y: u32, mask: u32) {
  if (mask == 0u) { return; }
  atomicAdd(&changeLog[genParams.slot], 1u);
  let t = tile_y * dims.tiles_x + tile_x;
  activate(t);
  if ((mask & CHANGED_WEST_EDGE) != 0u && tile_x > 0u)                { activate(t - 1u); }
  if ((mask & CHANGED_EAST_EDGE) != 0u && tile_x + 1u < dims.tiles_x) { activate(t + 1u); }
  if ((mask & CHANGED_NORTH_EDGE) != 0u && tile_y > 0u)               { activate(t - dims.tiles_x); }
  if ((mask & CHANGED_SOUTH_EDGE) != 0u && tile_y + 1u < dims.tiles_y) { activate(t + dims.tiles_x); }
}

// 全タイルを計算する (dispatch = tiles_x × tiles_y)。リストは使わない
@compute @workgroup_size(16, 16)
fn step_dense(@builtin(workgroup_id) wg: vec3u,
              @builtin(local_invocation_id) lid: vec3u,
              @builtin(local_invocation_index) li: u32) {
  let mask = stepTile(wg.x, wg.y, lid, li);
  if (li == 0u && mask != 0u) { atomicAdd(&changeLog[genParams.slot], 1u); }
}

// 全タイルを計算し、次世代のアクティブリストを作る (dispatch = tiles_x × tiles_y)。
// argsNext / argsFree はホストが事前に ARGS_CONTROL_WORKGROUPS に初期化しておく
@compute @workgroup_size(16, 16)
fn step_full(@builtin(workgroup_id) wg: vec3u,
             @builtin(local_invocation_id) lid: vec3u,
             @builtin(local_invocation_index) li: u32) {
  let mask = stepTile(wg.x, wg.y, lid, li);
  if (li == 0u) { publishChange(wg.x, wg.y, mask); }
}

// アクティブリストのタイルだけを計算する (dispatch = argsCur.x = 1 + タイル数)。
// workgroup 0 は制御用: 次々世代のリスト (argsFree) を空にする。
// タイル数 0 でも制御用 workgroup は必ず走るので、空リストが続いても argsFree のリセットは漏れない
@compute @workgroup_size(16, 16)
fn step_list(@builtin(workgroup_id) wg: vec3u,
             @builtin(local_invocation_id) lid: vec3u,
             @builtin(local_invocation_index) li: u32) {
  if (wg.x < ARGS_CONTROL_WORKGROUPS) {
    if (li == 0u) { atomicStore(&argsFree.x, ARGS_CONTROL_WORKGROUPS); }
    return;
  }
  let t = listCur[wg.x - ARGS_CONTROL_WORKGROUPS];
  let tile_x = t % dims.tiles_x;
  let tile_y = t / dims.tiles_x;
  let mask = stepTile(tile_x, tile_y, lid, li);
  if (li == 0u) { publishChange(tile_x, tile_y, mask); }
}
