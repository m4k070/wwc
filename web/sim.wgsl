// WireLevel WGSL compute shader
// Cell encoding (matches WireLevel.encodeCell):
//   bit 7-5: kind (0=Empty 1=Pin 2=Wire 3=Nand 4=Cross 5=Dff)
//   bit 4-3: dir (E=0 W=1 N=2 S=3)
//     Cross: bit4=hDir(E=0,W=1), bit3=vDir(N=0,S=1)
//   bit 1-0: levels
//     Wire/Nand/Pin: bit0=level
//     Cross: bit1=vv, bit0=hv
//     Dff:   bit1=prevClk, bit0=q

// Directions
const E: u32 = 0u;
const W: u32 = 1u;
const N: u32 = 2u;
const S: u32 = 3u;

// Kind constants
const K_EMPTY: u32 = 0u;
const K_PIN:   u32 = 1u;
const K_WIRE:  u32 = 2u;
const K_NAND:  u32 = 3u;
const K_CROSS: u32 = 4u;
const K_DFF:   u32 = 5u;

struct Params {
    width: u32,
    height: u32,
    // bit 0: clk_phase (0=low, 1=high)
    // bit 8+: reserved
    flags: u32,
}

@group(0) @binding(0) var<storage, read>     src  : array<u32>;
@group(0) @binding(1) var<storage, read_write> dst  : array<u32>;
@group(0) @binding(2) var<uniform>           dims : Params;

fn cellAt(src: ptr<function, array<u32>>, i: u32) -> u32 {
    let wordIdx = i >> 2u;
    let shift = (i & 3u) * 8u;
    return ((*src)[wordIdx] >> shift) & 0xFFu;
}

fn cellAtStorage(src: array<u32>, i: u32) -> u32 {
    let wordIdx = i >> 2u;
    let shift = (i & 3u) * 8u;
    return (src[wordIdx] >> shift) & 0xFFu;
}

fn setCell(dst: ptr<function, array<u32>>, i: u32, val: u32) {
    let wordIdx = i >> 2u;
    let shift = (i & 3u) * 8u;
    let mask = ~(0xFFu << shift);
    (*dst)[wordIdx] = ((*dst)[wordIdx] & mask) | (val << shift);
}

fn kind(c: u32) -> u32 { return (c >> 5u) & 7u; }
fn dir(c: u32) -> u32 { return (c >> 3u) & 3u; }
fn level(c: u32) -> u32 { return c & 1u; }

fn opposite(d: u32) -> u32 {
    switch (d) {
        case E: { return W; }
        case W: { return E; }
        case N: { return S; }
        default: { return N; }
    }
}

fn delta(d: u32) -> vec2<i32> {
    switch (d) {
        case E: { return vec2( 1,  0); }
        case W: { return vec2(-1,  0); }
        case N: { return vec2( 0, -1); }
        default: { return vec2( 0,  1); }
    }
}

fn presentedTo(c: u32, toward: u32) -> u32 {
    let k = kind(c);
    if (k == K_EMPTY) {
        // Empty presents nothing
        return 0xFFFFFFFFu;
    }
    if (k == K_PIN || k == K_WIRE || k == K_NAND) {
        return level(c);
    }
    // DFF: presents q (bit 0) in all directions
    if (k == K_DFF) {
        return c & 1u;
    }
    // Cross: presents hLevel toward hDir, vLevel toward vDir, nothing to other sides
    if (k == K_CROSS) {
        let hDir = select(E, W, ((c >> 4u) & 1u) == 1u);
        let vDir = select(N, S, ((c >> 3u) & 1u) == 1u);
        let hLevel = c & 1u;
        let vLevel = (c >> 1u) & 1u;
        if (toward == hDir) { return hLevel; }
        if (toward == vDir) { return vLevel; }
        return 0xFFFFFFFFu;
    }
    return 0xFFFFFFFFu;
}

fn pullFrom(src: array<u32>, x: i32, y: i32, side: u32) -> u32 {
    let d = delta(side);
    let nx = x + d.x;
    let ny = y + d.y;
    if (nx < 0 || ny < 0 || nx >= i32(dims.width) || ny >= i32(dims.height)) {
        return 0xFFFFFFFFu;
    }
    let ni = u32(ny) * dims.width + u32(nx);
    let nb = cellAtStorage(src, ni);
    return presentedTo(nb, opposite(side));
}

fn stepCell(src: array<u32>, x: i32, y: i32) -> u32 {
    let i = u32(y) * dims.width + u32(x);
    let cell = cellAtStorage(src, i);

    let k = kind(cell);
    if (k == K_EMPTY || k == K_PIN) {
        return cell; // unchanged
    }

    if (k == K_WIRE) {
        let d = dir(cell);
        let v = pullFrom(src, x, y, opposite(d));
        let levelVal = select(0u, 1u, v == 1u);
        return (cell & 0xFFu8) | levelVal; // replace only bit 0
    }

    if (k == K_NAND) {
        let d = dir(cell);
        var allTrue: bool = true;
        var anyInput: bool = false;
        for (var s: u32 = 0u; s < 4u; s = s + 1u) {
            if (s != d) {
                let v = pullFrom(src, x, y, s);
                if (v != 0xFFFFFFFFu) {
                    anyInput = true;
                    if (v == 0u) { allTrue = false; }
                }
            }
        }
        let levelVal = select(1u, 0u, anyInput && allTrue);
        return (cell & 0xF8u) | levelVal;
    }

    if (k == K_CROSS) {
        let hd = select(E, W, ((cell >> 4u) & 1u) == 1u);
        let vd = select(N, S, ((cell >> 3u) & 1u) == 1u);
        let hv = pullFrom(src, x, y, opposite(hd));
        let vv = pullFrom(src, x, y, opposite(vd));
        let hLevel = select(0u, 1u, hv == 1u);
        let vLevel = select(0u, 1u, vv == 1u);
        // Cross: keep kind+dir bits, replace level bits
        return (cell & 0xF8u) | (vLevel << 1u) | hLevel;
    }

    if (k == K_DFF) {
        let d = dir(cell);
        let dIn = pullFrom(src, x, y, opposite(d));
        let dVal = select(0u, 1u, dIn == 1u);
        // CLK from side(s) perpendicular to output direction
        var clk: bool = false;
        for (var s: u32 = 0u; s < 4u; s = s + 1u) {
            // sides perpendicular to dir
            let isPerp = (d == E || d == W) && (s == N || s == S) ||
                         (d == N || d == S) && (s == E || s == W);
            if (isPerp) {
                let v = pullFrom(src, x, y, s);
                if (v == 1u) { clk = true; }
            }
        }
        let prevClk = (cell >> 1u) & 1u;
        let q = (clk && prevClk == 0u) ? dVal : (cell & 1u);
        let newPrevClk = select(0u, 1u, clk);
        return (cell & 0xF8u) | (newPrevClk << 1u) | q;
    }

    return cell;
}

@compute @workgroup_size(16, 16)
fn step(@builtin(global_invocation_id) gid: vec3u) {
    let x = i32(gid.x);
    let y = i32(gid.y);

    if (x >= i32(dims.width) || y >= i32(dims.height)) {
        return;
    }

    let i = u32(y) * dims.width + u32(x);
    let newCell = stepCell(src, x, y);
    setCell(&dst, i, newCell);
}
