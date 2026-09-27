// WireLevel WGSL — level-driven, pull-type directional wiring.
//   naga restriction: ptr<storage,...> cannot be a function parameter.
//   All storage access goes through globals b1 (read) / b2 (write).
//
// セル規則 rule() は「中心セル + 4 近傍の生の値」の純粋関数。グリッド外の近傍は Empty (0) として渡す
// (presentedTo は Empty にもグリッド外にも「信号なし」を返すので同じ意味)。
//
// エントリポイント:
//   step_dense   1 dispatch = 1 世代、全タイルを計算する (参照実装。--engine dense)
//   block_full   全タイルを genParams.gens 世代 (ホストは 1 を渡す) 進め、次のアクティブリストを作る
//                (初回と、ホストがピンを書き換えた直後)
//   block_list   アクティブリストのタイルだけを genParams.gens (<= BLOCK_GENS) 世代進める
//                (dispatch_workgroups_indirect)
//   pack_bytes   読み戻し用: b1 (現在の世代) のセルを 1 バイトずつ u32 に 4 個詰める (世代は進めない)
// どれも「世代 slot に変化したタイル数」を changeLog[slot] に足す
// (changeLog[slot] == 0 ⇔ その世代で step(g) == g)。
//
// ブロック計算 (block_*): workgroup (16x16) = 1 タイル。タイルと周囲 BLOCK_GENS セル (halo) を shared memory
// に読み、k 世代を shared memory 内で進めて、内側 16x16 だけを書き戻す。von Neumann 近傍 1 セルなので、
// halo の外縁から j セル内側のセルは j 世代目まで正しく、内側 16x16 (外縁から BLOCK_GENS 以上) は k 世代後まで正しい。
//
// アクティブ化 (ブロック B = 世代 t..t+k-1 でタイル T を計算しなくてよい条件):
//   世代 g にセル c が変化するには、世代 g-1 に c の近傍 (c 自身を含む) のどれかが変化している必要がある
//   (近傍が不変なら rule の結果も不変)。これを遡ると、ブロック B 内で T のセルが変化するには、直前の世代
//   t-1 に T からマンハッタン距離 k 以内のセルが変化していなければならない。よって
//     (1) 前のブロックの最後の世代に、距離 BLOCK_GENS 以内 (自身と 8 近傍タイル) で変化があったタイル
//   を計算すれば、計算しないタイルは B の間ずっと不変。さらに ping-pong の両バッファを一致させるため
//     (2) 前のブロックの最初と最後で値が変わったタイル (片方のバッファにだけ新しい値がある)
//   も計算する。(1)(2) のどちらでもないタイルは両バッファとも g_t と同じ値を持つので、書かなくてよい。
const E: u32 = 0u; const W: u32 = 1u; const N: u32 = 2u; const S: u32 = 3u;
const K_EMPTY: u32 = 0u; const K_PIN: u32 = 1u; const K_WIRE: u32 = 2u;
const K_NAND: u32 = 3u; const K_CROSS: u32 = 4u; const K_DFF: u32 = 5u;

// タイルの一辺 (= workgroup_size)。gpu.rs の TILE_SIZE と一致させる
const TILE: u32 = 16u;
// BLOCK_GENS (1 dispatch で進める最大世代数 = halo の幅) は gpu.rs が先頭に const として付け足す

// stamp_base: このバッチの世代 slot のスタンプは stamp_base + slot + 1 (単調増加。stamps の比較に使う)
struct Dims { width: u32, height: u32, tiles_x: u32, tiles_y: u32, stamp_base: u32, }
// dispatch ごとのパラメータ (dynamic offset で選ぶ)。slot = この dispatch の最初の世代のバッチ内番号、
// gens = この dispatch で進める世代数 (step_dense は 1 固定で gens は見ない)
struct GenParams { slot: u32, gens: u32, }
// dispatch_workgroups_indirect の引数。x = 1 (制御用 workgroup) + アクティブタイル数
struct DispatchArgs { x: atomic<u32>, y: u32, z: u32, }

// タイルのアクティブ化マスク。ACTIVATE_SELF 以外は「次のブロックで影響が届く隣接タイル」
const ACTIVATE_SELF: u32 = 1u;
const ACTIVATE_W: u32 = 2u;
const ACTIVATE_E: u32 = 4u;
const ACTIVATE_N: u32 = 8u;
const ACTIVATE_S: u32 = 16u;
const ACTIVATE_NW: u32 = 32u;
const ACTIVATE_NE: u32 = 64u;
const ACTIVATE_SW: u32 = 128u;
const ACTIVATE_SE: u32 = 256u;
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
// 4 近傍の生のセル値 (グリッド外 / 計算範囲外は 0 = Empty)
struct Nbhd { e: u32, w: u32, n: u32, s: u32, }

fn neighborCell(nb: Nbhd, side: u32) -> u32 {
  switch (side) { case E { return nb.e; } case W { return nb.w; } case N { return nb.n; } default { return nb.s; } }
}
// side 側の近傍が自分に向けて出している値 (0/1、信号なしは 0xFFFFFFFF)
fn pull(nb: Nbhd, side: u32) -> u32 {
  return presentedTo(neighborCell(nb, side), opposite(side));
}

// WireLevel の 1 世代 (F# WireLevel.step の 1 セル分)
fn rule(cell: u32, nb: Nbhd) -> u32 {
  let k = kind(cell);
  if (k == K_EMPTY || k == K_PIN) { return cell; }
  if (k == K_WIRE) {
    return (cell & 0xF8u) | select(0u, 1u, pull(nb, opposite(dir(cell))) == 1u);
  }
  if (k == K_NAND) {
    let d = dir(cell);
    var allTrue: bool = true; var anyInput: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      if (s != d) {
        let v = pull(nb, s);
        if (v != 0xFFFFFFFFu) { anyInput = true; if (v == 0u) { allTrue = false; } }
      }
    }
    return (cell & 0xF8u) | select(0u, 1u, anyInput && !allTrue);
  }
  if (k == K_CROSS) {
    let hd = select(E, W, ((cell >> 4u) & 1u) == 1u);
    let vd = select(N, S, ((cell >> 3u) & 1u) == 1u);
    let hv = pull(nb, opposite(hd));
    let vv = pull(nb, opposite(vd));
    return (cell & 0xF8u) | (select(0u, 1u, vv == 1u) << 1u) | select(0u, 1u, hv == 1u);
  }
  if (k == K_DFF) {
    let d = dir(cell);
    let dVal = select(0u, 1u, pull(nb, opposite(d)) == 1u);
    var clk: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      let isPerp = ((d == E || d == W) && (s == N || s == S)) || ((d == N || d == S) && (s == E || s == W));
      if (isPerp) { if (pull(nb, s) == 1u) { clk = true; } }
    }
    let prevClk = (cell >> 1u) & 1u;
    let q = select(cell & 1u, dVal, clk && prevClk == 0u);
    return (cell & 0xF8u) | (select(0u, 1u, clk) << 1u) | q;
  }
  return cell;
}

// ---- 1 dispatch = 1 世代 (step_dense) -------------------------------------------------------

// グリッド上 (x, y) の side 側の近傍セル (グリッド外は 0)
fn gridNeighbor(x: i32, y: i32, side: u32) -> u32 {
  let d = delta(side);
  let nx = x + d.x; let ny = y + d.y;
  if (nx < 0 || ny < 0 || nx >= i32(dims.width) || ny >= i32(dims.height)) { return K_EMPTY; }
  return readBuf(u32(ny) * dims.width + u32(nx));
}
fn stepCell(x: i32, y: i32) -> u32 {
  let cell = readBuf(u32(y) * dims.width + u32(x));
  let nb = Nbhd(gridNeighbor(x, y, E), gridNeighbor(x, y, W), gridNeighbor(x, y, N), gridNeighbor(x, y, S));
  return rule(cell, nb);
}

// タイル内で 1 セルでも変化したら非 0 (workgroup 共有)
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

// 全タイルを 1 世代進める (dispatch = tiles_x × tiles_y)
@compute @workgroup_size(16, 16)
fn step_dense(@builtin(workgroup_id) wg: vec3u,
              @builtin(local_invocation_id) lid: vec3u,
              @builtin(local_invocation_index) li: u32) {
  let changed = stepTile(wg.x, wg.y, lid, li);
  if (li == 0u && changed) { atomicAdd(&changeLog[genParams.slot], 1u); }
}

// ---- 1 dispatch = k 世代のブロック計算 (block_full / block_list) ------------------------------

const REGION: u32 = TILE + 2u * BLOCK_GENS;          // halo を含む計算範囲の一辺
const REGION_CELLS: u32 = REGION * REGION;
const THREADS: u32 = TILE * TILE;
const CELLS_PER_THREAD: u32 = (REGION_CELLS + THREADS - 1u) / THREADS;

// 計算範囲の 2 面 (ping-pong)。面 p は region[p * REGION_CELLS ..]
var<workgroup> region: array<u32, 2u * REGION_CELLS>;
// bit j = 内側 16x16 が世代 slot+j に変化した
var<workgroup> subgenChanged: atomic<u32>;
// ACTIVATE_* の集計
var<workgroup> activation: atomic<u32>;

// 面 plane の計算範囲座標 (rx, ry) のセル (範囲外は 0)
fn regionCell(plane: u32, rx: i32, ry: i32) -> u32 {
  if (rx < 0 || ry < 0 || rx >= i32(REGION) || ry >= i32(REGION)) { return K_EMPTY; }
  return region[plane + u32(ry) * REGION + u32(rx)];
}

// 内側の位置 (lx, ly) のセルがブロックの最後の世代に変化したとき、次のブロックで影響が届くタイル。
// 隣接タイルまでのマンハッタン距離が BLOCK_GENS 以下なら届く
fn activationMask(lx: u32, ly: u32) -> u32 {
  let toW = lx + 1u; let toE = TILE - lx; let toN = ly + 1u; let toS = TILE - ly;
  var m = ACTIVATE_SELF;
  if (toW <= BLOCK_GENS) { m = m | ACTIVATE_W; }
  if (toE <= BLOCK_GENS) { m = m | ACTIVATE_E; }
  if (toN <= BLOCK_GENS) { m = m | ACTIVATE_N; }
  if (toS <= BLOCK_GENS) { m = m | ACTIVATE_S; }
  if (toW + toN <= BLOCK_GENS) { m = m | ACTIVATE_NW; }
  if (toE + toN <= BLOCK_GENS) { m = m | ACTIVATE_NE; }
  if (toW + toS <= BLOCK_GENS) { m = m | ACTIVATE_SW; }
  if (toE + toS <= BLOCK_GENS) { m = m | ACTIVATE_SE; }
  return m;
}

// タイル (tile_x, tile_y) を genParams.gens 世代進めて内側を b2 に書く。
// 戻り値は workgroup 変数 subgenChanged / activation に残す。全スレッドが同じ回数 barrier を通る。
fn stepBlock(tile_x: u32, tile_y: u32, li: u32) {
  if (li == 0u) { atomicStore(&subgenChanged, 0u); atomicStore(&activation, 0u); }
  let ox = i32(tile_x * TILE) - i32(BLOCK_GENS);
  let oy = i32(tile_y * TILE) - i32(BLOCK_GENS);
  for (var m: u32 = 0u; m < CELLS_PER_THREAD; m = m + 1u) {
    let r = li + m * THREADS;
    if (r < REGION_CELLS) {
      let gx = ox + i32(r % REGION);
      let gy = oy + i32(r / REGION);
      let inGrid = gx >= 0 && gy >= 0 && gx < i32(dims.width) && gy < i32(dims.height);
      region[r] = select(K_EMPTY, b1[u32(gy) * dims.width + u32(gx)], inGrid);
    }
  }
  workgroupBarrier();

  let gens = genParams.gens;
  for (var j: u32 = 0u; j < gens; j = j + 1u) {
    let src = (j % 2u) * REGION_CELLS;
    let dst = REGION_CELLS - src;
    for (var m: u32 = 0u; m < CELLS_PER_THREAD; m = m + 1u) {
      let r = li + m * THREADS;
      if (r < REGION_CELLS) {
        let rx = i32(r % REGION);
        let ry = i32(r / REGION);
        let cell = region[src + r];
        let nb = Nbhd(regionCell(src, rx + 1, ry), regionCell(src, rx - 1, ry),
                      regionCell(src, rx, ry - 1), regionCell(src, rx, ry + 1));
        let after = rule(cell, nb);
        region[dst + r] = after;
        let lx = rx - i32(BLOCK_GENS);
        let ly = ry - i32(BLOCK_GENS);
        let inner = lx >= 0 && ly >= 0 && lx < i32(TILE) && ly < i32(TILE);
        if (inner && after != cell) {
          atomicOr(&subgenChanged, 1u << j);
          if (j + 1u == gens) { atomicOr(&activation, activationMask(u32(lx), u32(ly))); }
        }
      }
    }
    workgroupBarrier();
  }

  // 内側 16x16 を書き戻す (1 スレッド 1 セル)。ブロックの前後で値が変わったら自身を次も計算する
  let lx = li % TILE;
  let ly = li / TILE;
  let x = tile_x * TILE + lx;
  let y = tile_y * TILE + ly;
  if (x < dims.width && y < dims.height) {
    let i = y * dims.width + x;
    let final_ = region[(gens % 2u) * REGION_CELLS + (ly + BLOCK_GENS) * REGION + lx + BLOCK_GENS];
    b2[i] = final_;
    if (final_ != b1[i]) { atomicOr(&activation, ACTIVATE_SELF); }
  }
  workgroupBarrier();
}

// タイル t を次のブロックのアクティブリストに積む (この dispatch で既に積まれていれば何もしない)
fn activate(t: u32) {
  let stamp = dims.stamp_base + genParams.slot + 1u;
  let prev = atomicMax(&stamps[t], stamp);
  if (prev < stamp) {
    let idx = atomicAdd(&argsNext.x, 1u);
    listNext[idx - ARGS_CONTROL_WORKGROUPS] = t;
  }
}

// ブロックの結果を公開する (li == 0 のスレッドだけが呼ぶ): 世代ごとの変化を changeLog に足し、
// activation に従ってタイルを次のリストに積む
fn publishBlock(tile_x: u32, tile_y: u32) {
  let changed = atomicLoad(&subgenChanged);
  for (var j: u32 = 0u; j < genParams.gens; j = j + 1u) {
    if ((changed & (1u << j)) != 0u) { atomicAdd(&changeLog[genParams.slot + j], 1u); }
  }
  let m = atomicLoad(&activation);
  if (m == 0u) { return; }
  let t = tile_y * dims.tiles_x + tile_x;
  let hasW = tile_x > 0u;
  let hasE = tile_x + 1u < dims.tiles_x;
  let hasN = tile_y > 0u;
  let hasS = tile_y + 1u < dims.tiles_y;
  if ((m & ACTIVATE_SELF) != 0u) { activate(t); }
  if ((m & ACTIVATE_W) != 0u && hasW) { activate(t - 1u); }
  if ((m & ACTIVATE_E) != 0u && hasE) { activate(t + 1u); }
  if ((m & ACTIVATE_N) != 0u && hasN) { activate(t - dims.tiles_x); }
  if ((m & ACTIVATE_S) != 0u && hasS) { activate(t + dims.tiles_x); }
  if ((m & ACTIVATE_NW) != 0u && hasN && hasW) { activate(t - dims.tiles_x - 1u); }
  if ((m & ACTIVATE_NE) != 0u && hasN && hasE) { activate(t - dims.tiles_x + 1u); }
  if ((m & ACTIVATE_SW) != 0u && hasS && hasW) { activate(t + dims.tiles_x - 1u); }
  if ((m & ACTIVATE_SE) != 0u && hasS && hasE) { activate(t + dims.tiles_x + 1u); }
}

// 全タイルを計算し、次のアクティブリストを作る (dispatch = tiles_x × tiles_y)。
// argsNext / argsFree はホストが事前に ARGS_CONTROL_WORKGROUPS に初期化しておく
@compute @workgroup_size(16, 16)
fn block_full(@builtin(workgroup_id) wg: vec3u,
              @builtin(local_invocation_index) li: u32) {
  stepBlock(wg.x, wg.y, li);
  if (li == 0u) { publishBlock(wg.x, wg.y); }
}

// アクティブリストのタイルだけを計算する (dispatch = argsCur.x = 1 + タイル数)。
// workgroup 0 は制御用: 次々 dispatch のリスト (argsFree) を空にする。
// タイル数 0 でも制御用 workgroup は必ず走るので、空リストが続いても argsFree のリセットは漏れない
@compute @workgroup_size(16, 16)
fn block_list(@builtin(workgroup_id) wg: vec3u,
              @builtin(local_invocation_index) li: u32) {
  if (wg.x < ARGS_CONTROL_WORKGROUPS) {
    if (li == 0u) { atomicStore(&argsFree.x, ARGS_CONTROL_WORKGROUPS); }
    return;
  }
  let t = listCur[wg.x - ARGS_CONTROL_WORKGROUPS];
  stepBlock(t % dims.tiles_x, t / dims.tiles_x, li);
  if (li == 0u) { publishBlock(t % dims.tiles_x, t / dims.tiles_x); }
}

// ---- 読み戻し用のバイト詰め (pack_bytes) ----------------------------------------------------

// packed[w] = セル 4w..4w+3 の下位 8 ビット (リトルエンディアンでセル順のバイト列になる)
@group(1) @binding(0) var<storage, read_write> packed : array<u32>;

const PACK_WORKGROUP: u32 = 256u;

@compute @workgroup_size(256)
fn pack_bytes(@builtin(workgroup_id) wg: vec3u,
              @builtin(num_workgroups) nwg: vec3u,
              @builtin(local_invocation_index) li: u32) {
  let w = (wg.y * nwg.x + wg.x) * PACK_WORKGROUP + li;
  let cellCount = dims.width * dims.height;
  if (w * 4u >= cellCount) { return; }
  var word = 0u;
  for (var b: u32 = 0u; b < 4u; b = b + 1u) {
    let i = w * 4u + b;
    if (i < cellCount) { word = word | ((b1[i] & 0xFFu) << (8u * b)); }
  }
  packed[w] = word;
}
