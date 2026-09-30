# WireLevel コンパイラ TODO

## 現在のテスト結果: F# 421/421 / cargo test (wgpu-runner) 49/49 / GPU golden 24/24 / memory-test.sh 5/5 / Playwright 24/24 PASS
(2026-09-28 実測。F# は PR #5 時点の 287 からタイミング駆動配置・クロックピン分割 (ClockPinSplitTests) 分が増えて 421、
cargo test は meta v3 対応で 49。GPU golden はこのブランチで再計測して 24/24、Playwright は d008cbe, 2026-08-14 時点で未再計測)

## 現在の到達点 (2026-09-29)

### (2026-09-29) ブート ROM を CA で走らせる (段階 0) — ブランチ `bootrom-ca`、コミット `d34286e`

自作の最小ブート ROM (256 B, `bootrom/minimal.asm`) を CA (tiled, 14x12, 2 相) で走らせ、
ロゴを VRAM に展開して LCD を有効化するところまで到達した。

- メモリモデル拡張: VRAM (0x8000-0x9FFF)・I/O (0xFF00-0xFF7F)・LY (0xFF44、周期から算出、読み出し専用)・
  ブート ROM オーバーレイ (0xFF50 で解除)。program JSON は `memory.bootRom`、golden は `bootRomSha256`。
  F# (`src/Testbench.fs`) と Rust (`wgpu-runner/src/memory.rs`) の両方に同じ規則を入れる。
- ROM の仕事: ヘッダチェックサム検査 → ロゴ 48 B をニブル単位で VRAM $8010-$80CF へ 2bpp 展開 (12 タイル)
  → タイルマップ $9904 (画面中央) → BGP=$FC / SCY=$64 / LCDC=$91 → LY を $C000 に記録 →
  末尾 $00FC-$00FF で 0xFF50 を書き、PC が $0100 へ進む (実機と同じ終わり方)。
- ロゴ展開の規則: ニブル (4 px) を 8 px に拡大するとタイル 1 行にちょうどなる。48 B × 2 = 96 行 =
  12 タイル × 8 行 → 元の 2 倍幅 (96×8) で表示される。1 バイトで 2 タイル (上位/下位ニブル)。
- RTL のリセット PC が **0x0100** なので、カートリッジ側 $0100 に `jp $0000` (trampoline) を置き、
  解除後は $0000 の `jp $0200` からプログラムに入る (「同じ番地が解除前後で別のコードを返す」ことを
  実行で確認する形)。RTL を 0x0000 リセットに直せば trampoline は不要。
- 検証: RTL (NetlistSim) expect **210/210** / GPU **6000 周期 全一致 + expect 210/210** (4m30s) /
  F# **426/426** / cargo test 53/53 / run-tests.sh 24/24 / memory-test.sh 5/5 /
  34 本スイート 37/37 (82 秒、メモリモデル拡張の回帰なし)。
- 可視化: `src/RenderBootRom.fsx` が最終メモリを gbfs の PPU に食わせて PNG を出す。ROM は SCY=$64
  (スクロール前) で止めるので、ロゴを見るには `--scy 0` を使う。右端 2 ピクセルに点が 1 つ残る
  (原因未確定。gbfs のフレームバッファの残りと見ているが未検証)。
- コスト: この 1 本は **46.6 ms/サイクル** (他 34 本は 16.5 ms)。メモリ読み出し経路が長いため。
  6000 周期で 4m30s。GPU スイートの glob から外してある (`bootrom_minimal.json`、1 本 +4.5 分のため)。
- RTL は仕様より **1 命令あたり約 +1 サイクル**かかる (例: `ld a,[bc]` 3 / `and n` 3 / `jr nz` 3)。
  ROM 実測 57 サイクル/ソースバイト。既知の「サイクル精度」課題の実データ。

- **tiled エンジンの世代あたりコストを削る (残課題 1 (e))** (2026-09-29、ブランチ `gpu-block-gens`):
  1 dispatch で進める世代数 `BLOCK_GENS` を掃引した。**K=3 が最良で 34 本の GPU 照合は 112.6 → 78 秒 (−31%)**。
  K=1 は dispatch 律速 (daa_edge 単体 16.8 秒)、K≥8 は halo の再計算で悪化 (世代あたり K=3 7.1 µs /
  K=4 7.2 / K=8 10.9 / K=12 18.8 / K=14 24.8)。`checkInterval` (batch) は 8〜1024 で実時間が変わらない
  (同期のコストは無視できる)。検証は GPU 全周期照合 37/37 / `run-tests.sh` 24/24 /
  `memory-test.sh` (引数なし) 5/5 / cargo 49/49。

- **ピッチ縮小で 1 周期を短くする (残課題 1 (d))** (2026-09-28、ブランチ `pitch-14x12`):
  配線済みグリッドのクリティカルパスを分解すると 97% がゲート間の「配置距離 (セル数)」で、セル数はピッチに
  比例する。20x14 は行優先配置時代の名残なので、タイミング駆動配置で再探索した。**14x12 で配線完走**
  (10.0 分、rip-up 0、grid 1481x1273)。34 本の GPU 照合は 150.7 → **112.6 秒 (−25%)**、世代数 −25%
  (4 本の部分計測)、クロック最大到達は 284 / 282 → **186 / 196 世代**。16x12 も完走 (133.0 秒)、
  12x10 は輻輳失敗 (18,041/18,691 で探索上限)。検証は AnalyzeHold (2 相の不変条件 成立) /
  GPU 全周期照合 37/37 / `run-tests.sh` 24/24 / F# 421/421 / cargo 49/49 / LoadRouted 整合性・鮮度 OK。

- **クロックピンの複数化 (issue #7 (b))** (2026-09-28、ブランチ `multi-clock-pins`):
  clk_a / clk_b を各 16 本に分けて配線し直した (`--place anneal-timing --clocking two-phase --clock-pins 16`、
  16.3 分、rip-up 0、grid 2110x1480)。クロックの最大到達は 1,478 / 1,494 → **284 / 282 世代**、
  HALT 中の 1 周期は 3,020 → 608 世代、34 本の GPU 照合は 195 → **152 秒**。
  2 相の不変条件 (同相 DFF 間の組合せ経路 0 本) はそのまま成立する。検証は AnalyzeHold /
  GPU 全周期照合 37/37 / `run-tests.sh` 24/24 / `memory-test.sh` (引数なし) 5/5 / F# 421/421 / cargo 49/49。
  世代数は −46% だが実時間は −22% にとどまる (tiled エンジンは変化タイルだけを dispatch するため、ピンを増やすと
  同時に走る波面が増え、総タイル計算量は世代数ほど減らない)。

### (2026-09-27) PR #5「sm83_full を CA 上で完走・検証」

sm83_full（SM83 CPU フルセット）を初めて CA 上で配線完走させ、RTL の正しさと RTL≡CA を外部基準で確かめた。

```
blargg cpu_instrs 11/11 ──▶ RTL (NetlistSim + gbfs の周辺回路)
                            RTL ≡ CA (GPU 全周期照合 37/37)
```

- **配線完走**: sm83_full (組合せ 10,859 + DFF 181。2 相クロック化で DFF は 362 に倍化、
  最終 gateCount 11,221) を **20x14 ピッチ・アニーリング配置 (`--place anneal`)・2 相ノンオーバーラップ
  クロック (`--clocking two-phase`)** で配線。**16.1 分、rip-up 0** (`routed/sm83_full.log`)。
  行優先配置では 89% で輻輳失敗していた (下記「経緯」参照) — 配置をアニーリングに変えたことが決定打
- **RTL ≡ CA**: GPU 全周期照合 **37/37** (仕様テスト 34 本 + 検証器の自己テスト 3 本、`wgpu-runner/memory-test.sh` 一式で約 3 分)
- **RTL の正しさ (公開テスト ROM による外部基準)**: blargg `cpu_instrs` 個別版 **11/11 PASS**
  (`src/CoSimGbfs.fsx --lockstep`。gbfs の CPU と命令単位の不一致 0)。DAA は全数 4,096 件、
  全命令は 498 通り × 4 パターン、割込みシナリオは 200 本、いずれも gbfs (`../gbfs`) の参照 CPU と一致
- クロック木は最短経路木配線 + L1 ミニマックス中心のピン配置で、**最大到達時間が理論下限と一致**
  (clk_a 1,386 gen / clk_b 1,450 gen)
- GPU ランナー (wgpu-runner) を高速化: sm83_full 全 33 本の照合が 約 2 時間 → **191 秒**
  (収束判定を GPU 上で行う / 変化タイルだけ計算する tiled エンジン / クロック立ち下げの settle 削減 / クロック木最短経路化)
- 詳細な経緯・数値は PR #5 (squash commit 77ebd61) を参照

## 残課題 (優先度の高い順の目安)

1. **CA の高速化の続き**: クロックの到達 (ピン分割) まで終わった。次の律速は `data_in` 窓
   (メモリ入力を適用した後の組合せ収束) で、命令実行中の 1 周期の平均 8,237 世代の内訳は
   data_in 3,530 / phaseA 483 / phaseB 4,224 (phaseB ≒ data_in + 700)。候補:
   - (a) 外部入力ピンを左端 (X=0) から使用箇所の近くへ移す — #17 で実施済み、効果は小さかった (全 34 本の世代数 −1.7%)
   - (b) クロックピンを複数に分ける — **完了** (2026-09-28)。k=16 で HALT 中の 1 周期 3,020 → 608 世代、
     34 本の GPU 照合 195 → 152 秒 (世代数 −46% に対し実時間 −22%。理由は上記「現在の到達点」参照)
   - (c) クリティカルパスを考慮した配置 — #20 で実施済み (命令実行中の 1 周期の平均 15,062 → 9,621 世代)
   - (d) **完了** (2026-09-28): クリティカルパスを実測で分解し、候補を 3 つ測った。
     * クリティカルなネットを先に配線して回り道を削る → **上限が小さい**。配線済みグリッドの最長経路で
       回り道は 354 / 4,513 = 7.8% しかない (配置距離が 97%)
     * パス遅延を直接最小化する配置 → タイミング駆動配置の重みを掃引したが現行 (α=10 β=12) が最良。
       上げると悪化 (予測 1 周期: α=10 → 13,073 / α=100 → 13,067 / α=1000 → 13,939 / α=10000 → 15,759、
       β=4 は 16,929)。重み付き総和という目的関数の範囲では改善余地なし
     * **ピッチ縮小 → 効いた**。14x12 で 34 本の GPU 照合 150.7 → 112.6 秒 (−25%)。詳細は「現在の到達点」
     * ピッチの下限を実測: 16x12 = 133.0 秒 / **14x12 = 112.6 秒 (採用、rip-up 0、10.0 分)** /
       13x11 = 111.3 秒だが rip-up 255 回・世代数 +7% (実質同じなので不採用) / 12x10 は輻輳失敗。
       rip-up が入る領域では幾何の利得より配線の劣化が勝つ → **下限は 14x12 で確定**
   - (e) **tiled エンジンの `BLOCK_GENS` を実測で決める — 完了** (2026-09-29)。8 → 3 にするだけで
     34 本の GPU 照合 112.6 → **78 秒 (−31%)**
     * 続けて「光円錐で計算範囲を縮める」案 (世代 j では内側 (16+2(K-j))² だけ評価すればよい) を
       実装したが**効果なし**。K=3 では −32% のセル削減が範囲チェックのコストに食われて横ばい
       (11.38 秒 vs 11.32 秒)、K=8 で −17% が上限。縮小範囲を可変除数で走る実装はさらに悪化
       (11.27 秒 — GPU の整数除算が高コストで、定数除数 (REGION) の strength reduction が効かなくなる)
     * 分かったこと: 世代あたりコストは「アクティブタイル数 × タイルあたり走査セル数」に比例する
       (rule を外して CA を発散させると全タイルが毎世代変化し、世代あたり 88〜194 µs = 通常の 12〜27 倍)。
       ただし 1 セルあたり ~40 ps は命令スループットから期待される値の ~9 倍遅い
      * 続けて**タイル粒度 (TILE) を掃引 — 効果なし** (2026-09-29)。ブート ROM 400 周期
        (2,254,101 世代、grid 1481×1273) で TILE 8 / 16 / 32 を実測: **8,083 / 7,968 / 12,502 ns/世代**。
        世代数は全条件で同一なので比較は clean (ノイズ床は ±1%、baseline 再測 8,047)。
        **TILE=16 (現行) が既に最適**で、大きくすると薄い波面でも「変化していないセル」を含む
        タイルが活性化して再計算量が増え、57% 悪化する。TILE=8 はノイズと区別できない。
        → **タイル粒度はレバーではないので変更は破棄**。なお TILE=32 は workgroup が
        1024 スレッドになり wgpu の既定上限 (`maxComputeInvocationsPerWorkgroup=256`) を
        超えるため `required_limits: adapter.limits()` が必要になる (検証エラーで判明)
      * 残る候補は `rule` の branchless 化 (warp 発散の解消)。ただし変更タイル内の ~99% は
        Wire/Empty (ゲートは全セルの 0.6%) なので warp 内の分岐は少ない見込みで、効果は未検証。
        先に発散の実測をしてから着手するのが順序。**TILE=32 は実測で悪化したので候補から外す**
      * **世代あたりコストの内訳を計測** (2026-09-29、`WWC_STATS=1`)。gpu.rs に累積タイマを入れ、
        「dispatch 列の記録+submit (CPU 側)」と「読み戻しの map 待ち (未実行の GPU 仕事を含む)」を
        分けた。ブート ROM 400 周期 (2,254,101 世代、wall 17.90 s) で
        **encode 5.26 s (30%) / wait 12.14 s (70%)** = 世代あたり **CPU 2.3 µs + GPU 5.4 µs**。
        **両方が効いており、片方だけ削っても頭打ちになる** (TILE 掃引が効かなかった一因)
      * 換算: `settle()` は `run_until_settled(max_steps, check_interval)` を呼び、既定の
        check_interval=256 なので 1 バッチ = 86 dispatch (1 full + 255/3 block) + 1 読み戻し。
        よって **CPU は 1 dispatch あたり 6.9 µs** (757k dispatch で 5.26 s)、
        **GPU は 1 世代あたり 5.4 µs** (読み戻し待ちは 1 回 1.38 ms = 256 世代分の実行)。
        次の候補は CPU 側の `SUBMIT_CHUNK_DISPATCHES` (encoder/submit の粒度) の掃引 —
        dispatch 数が CPU 律速なので、粒度を上げれば encode が減る (代わりに GPU との重なりが減る)
      * 計測の常時コストは map/submit ごとの `Instant::now()` 2 回だけで無視できる。
        表示は `WWC_STATS=1` のときだけ (main.rs の `print_stats`、stderr)
      * **`SUBMIT_CHUNK_DISPATCHES` を掃引 — 既定の 16 が最良** (2026-09-29)。`WWC_CHUNK` で
        上書きできるようにして 1/4/16/64/256/1024 を実測 (ブート ROM 400 周期、計 = encode+wait):
        **25.55 / 17.47 / 17.16 / 19.08 / 19.11 / 18.81 秒**。encode は 16.88 → 7.75 → 5.14 →
        4.79 → 4.79 → 4.76 秒で **4.8 秒で飽和** = 757k dispatch を 1 個 6.3 µs で記録する床で、
        これ以上は粒度を上げても減らない。大きい側は wait が 12.0 → 14.3 秒に増えて負ける
        (submit をまとめると GPU が走り出せず、CPU との重なりが消える)。**既定変更なし**
      * 総時間の下限は「CPU 4.8 秒 (dispatch 数 × 6.3 µs)」と「GPU 5.4 µs/世代」の重ね合わせで、
        CHUNK=16 はほぼ最適点。**残るレバーは GPU 側のセルあたりコスト** (TILE は効かず、
        `rule` の branchless 化が候補)。ただし着手前に「1 世代あたり何セル走査しているか」を出す —
        changeLog の変化タイル数を足せば走査セル数が出るので、計算律速か起動律速かを切り分けられる
      * **走査セル数を計測 → 計算律速ではない** (2026-09-29)。changeLog の変化タイル数を足した:
        世代 2,261,555 / 変化タイル計 144,205,959 = **63.8 タイル/世代 ≒ 走査 10,266 セル/世代
        = 524 ps/セル**。RTX 3060 の理論スループット (~200 G セル/秒 = 5 ps/セル) の **~1%** しか
        出ていない。1 dispatch は 64 workgroup 程度しかなく GPU が埋まらないので、
        **セルあたりは命令数ではなくメモリレイテンシと占有で決まっている**
      * これで**安いマイクロ最適化は打ち止め**と判断: `rule` の branchless 化は効かない見込み
        (セルあたりはレイテンシ律速で命令数ではない)。TILE 掃引 (16 が最適) と CHUNK 掃引
        (16 が最適) が横ばいだったのも同じ理由。残るのは構造側 —
        (1) 世代数を減らす (クリティカルパス = 配置とピッチは既に限界) か、
        (2) dispatch を跨がず GPU 内で固定点まで回す (on-device fixpoint / megakernel) か
      * 計測コマンド: `WWC_STATS=1 ./wgpu-runner/target/release/wgpu-runner --memory routed/_bench_bootrom.json`
        (ベンチ `routed/_bench_bootrom.json` = bootrom_minimal.json の cycles を 400 にしたもの、未追跡)
      * **K を振ってコストモデルを実測** (2026-09-29)。前項の「走査 10,266 セル/世代 = 524 ps/セル」は
        `(TILE+2K)²/K` で割っていた誤り。block_* は **世代ごとに領域全体 (TILE+2K)² を再計算する**ので
        計算セル数はその K 倍。正しくは K=3 で **30,862 計算セル/世代 = 175 ps/セル**
        (グローバルメモリの読み書きは 10,287 セル/世代 = 領域のロード/ストアのみ)
      * K=1 と K=3 の 2 点で解くと **GPU 時間 ≒ 1.4 µs/dispatch + 0.16 ns/計算セル**。
        K=3 では計算セルが 91% を占め、**dispatch の起動・排出は 9% しかない**
      * K=1 の実測: 変化タイル/世代は 63.8 で K=3 と同じ (同一の計算)。GPU は
        **4.72 µs/世代 (K=3 は 5.40) と K=1 のほうが速い**。遅いのは CPU 側で
        encode 13.71 s = **6.1 µs/dispatch × 227 万回** (K=3 は 5.14 s / 757k = 6.8 µs/dispatch)。
        つまり **GPU は K=1 を好み、CPU は大きい K を好む**。K=3 はその妥協点で、
        wgpu の dispatch 記録コスト (6〜7 µs/dispatch) が実質の制約になっている
      * 結論 (2026-09-29): megakernel (dispatch を跨がず GPU 内で固定点まで回す) の伸びしろは
        **GPU 1.4 µs/dispatch + CPU 6.1 µs/dispatch の除去 = 最大 ~1.6 倍**で、数日規模の
        作り直しに見合わない。段階 2 (フル起動演出 3.5M サイクル) は 45 → 28 時間程度にしかならず、
        到達可能にはならない。**マイクロ最適化も構造変更も打ち止め**と判断し、高速化はここで止める
2. **`compileWL` / `ExportRouted.fsx` の既定値の見直し**: 既定は今も**単相・行優先** (`--place rowmajor`
   `--clocking single`)。sm83_full の配線に使った `--place anneal --clocking two-phase` を既定にするか検討する
   (小規模回路では行優先で十分なため、回路規模で自動判定する案もある)。ピッチも同じ: `pitchFor` は >3000 で
   16x12 を返すが、sm83_full は 14x12 で完走する (12x10 は輻輳失敗)。大規模の基本ピッチを 14x12 に下げるか、
   行優先のときだけ 16x12 に上げるかは要検討 (2026-09-28)。
3. **RTL のサイクル精度**: sm83_full は内部処理の M サイクルを持たない (例: `JP nn` は実機 4 M サイクル、
   RTL は 3 バスサイクル。平均すると約 2.9 周期/命令)。直すなら blargg `instr_timing` ROM が物差しになる
   (`/home/makoto/work/gb-test-roms/MANIFEST.md` の「タイミングを試す」区分)
4. **mooneye acceptance の命令系を RTL で流す**: `daa`, `bits/reg_f`, `ei_sequence`, `ei_timing`
   (EI の 1 命令遅延), `rapid_di_ei`, `halt_ime0_ei`, `interrupts/ie_push`, `if_ie_registers` など。
   対象の一覧は `/home/makoto/work/gb-test-roms/MANIFEST.md` の「命令の意味を試す」区分
5. **STOP 命令は未実装**。ジョイパッド入力線で復帰する仕様のため、周辺回路の仕様を決めてから実装する
   (GB ソフトでの使用は稀)
6. **`NetlistSim` の高速化**: 現在 約 2,600 周期/秒。`apply` のたびに全ネット配列をコピーし全ゲートを
   評価しており、差分評価にすれば大幅に速くなる見込み (blargg 11 本の合計実行時間が数時間かかっている)
7. **gbfs 側 (`../gbfs`) の既知の不具合 3 件**: 公開テスト ROM で見つかったもの — (a) `INC/DEC r8` の
   サイクル数が 12 になっている、(b) IF の上位 3 ビットが 1 で読めない (未使用ビットは本来 1 固定のはずが
   0 で読める)、(c) `ie_push` 系。(b) は wwc の `Testbench.fs` も gbfs に合わせて IF 上位ビットを 0 として
   読んでいるため、直すなら両方同時に直す必要がある。gbfs のテスト ROM ランナーは gbfs の `test-rom-runner` ブランチ
   (`remotes/origin/test-rom-runner`) にあり、PR は未作成。また gbfs の `src/gbfs.Lib/WireLevelCpu.fs` は
   中身がネイティブ CPU への委譲だけのスタブで (`WireLevelCpu.fs:63` にコメントあり)、名前と実体が一致していない。
   CA を gbfs の CPU として実時間で動かすのは 1 周期 0.03〜0.05 秒でも 1 フレームに数分〜十数分かかり非現実的
8. `web/sm83_mc_*.bin` (2026-06-13 生成) はクロック未接続バグ入りの古い回路のまま — 再生成が必要 (P3 参照)
9. `placement-hilbert` ブランチ (ローカル + `remotes/origin/placement-hilbert`) はヒルベルト配置の負の結果の
   記録として残している。採用しない。理由: 総配線長は約 7% 短くなるのに、sm83_full 28x20 の配線到達点が
   16,016 → 13,600 端子に下がり、sm83_subset でも面積比 (需要/bbox) が 1.427 → 1.518 に悪化したため
10. 決定待ち (未解決): プログラム・ROM を `routed/` から `programs/` に分けるか (DESIGN-VERIFY.md §8 Q2)。
    現状も `routed/rom_sm83_full_*.bin` のように配線成果物と混在している
11. 未着手: B-5 RTL (Verilog) との照合 (`yosys sim -vcd` による VCD 比較)。今回は代わりに公開テスト ROM +
    NetlistSim + gbfs 周辺回路での相互検証 (blargg 11/11、gbfs 差分 0 件) で RTL の正しさを確認したため、
    当初計画の VCD 比較の必要性は下がっているが、正式には未実施

---

## 次の一手 (M8: SM83 フルセット)

方針: 長時間配線 (subset 約 100 分 / full 5〜8 時間) の成果を捨てないよう、
**保存 → 検証の道具を先に揃え、安い sm83_subset で全工程を通してから sm83_full に進む**。

### Step A: 配線成果物の保存と再利用 ✅ 完了 (2026-09-15)

- [x] `src/RoutedArtifact.fs`: 配線結果を `routed/<circuit>.bin` (exportGrid 形式) +
      `routed/<circuit>.meta.json` として保存・再読込
      * meta: ポート名 → ビット毎の座標 (.bin と同じ正規化座標、LSB first)、
        元 verilog JSON の SHA-256、生成時の git commit (`+dirty` 付き)
      * 出力ビットは `CellProbe` (ゲート出力セル/ピン) / `ConstProbe` / `Unobservable` の DU。
        yosys の定数ビット ("0"/"1") を位置を保って保持する
        (`Pipeline.parseYosysJson` は定数を捨てるためビット位置がずれる。sm83_min の
        b_out[6:7]、sm83_full の flags 下位 4bit が該当)
      * 読込時に寸法・ファイル長・プローブ先セル種別を検証、`ensureFresh` で
        verilog JSON の変更を検出。書込は一時ファイル経由で置換
- [x] `src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR]`: 配線 → 保存 → 再読込確認。
      ポート解析を配線前に済ませ、meta 生成に失敗しても grid を `.rescue.bin` に退避
- [x] `src/LoadRouted.fsx <circuit> [--dir DIR]`: 整合性・鮮度の確認 (Step B/C の雛形)。
      終了コード 0=OK / 1=エラー / 3=verilog JSON 変更
- [x] テスト `RoutedArtifactTest` 8 件 (counter4 を保存 → 読込後、再配線なしで 0→1→2 と数える)
- [x] 動かなかった `TestSm83Subset.fsx` と、結果を捨てる `TestSm83Full.fsx` を削除
- [x] `routed/*.bin` / `*.meta.json` は git 管理する (長時間配線の結果を vega/moon 間で共有するため。
      sm83_min で 100KB、subset/full は数 MB 見込み)

### Step B: メモリバス対応の命令レベル GPU 検証 — 設計: [DESIGN-VERIFY.md](DESIGN-VERIFY.md)

方針: 期待値は手書きモデルではなく**合成済みネットリストのゲートレベルシミュレーション**。
プログラム JSON を F# と runner `--memory` で共有し、F# が生成した golden と全周期を照合する。

- [x] wgpu-runner `--memory` (ROM/RAM 駆動のスモーク) と sm83_subset スモーク 2/2 PASS (2406b0a, a1d8501)
- [x] B-3a `--memory` の堅牢化 (2026-09-16): meta の定数/観測不能ビットの読込、`expect` の未知ポート名・
      値の幅超え・観測不能ビットを GPU 実行前にエラー、未収束周期 (リセット・data_in 伝播含む) を失敗扱い、
      meta のフィールド名 (`gateCount` 等) と `formatVersion` の確認。`cargo test` 8 件、smoke 2/2 PASS 維持
- [x] smoke の `maxStepsPerPhase` を 12000 → 20000 (cycle 0 high が 12079 世代で、旧上限では未収束だった)
- [x] B-1 `src/NetlistSim.fs` (2026-09-16): NAND/NOT/DFF の周期シミュレータ。CA と同じ規則
      (入力 0 本の NAND は 0、DFF は clk 立ち上がりで D、初期値 0) で、`apply` 1 回 = CA の settle 1 回。
      閉路・未駆動入力・主クロック以外のクロック・未対応ゲートは明示的なエラー。
      テスト `NetlistSimTest` 11 件: counter4、alu4 全 1024 入力、**sm83_min 20/20 (GPU 検証済みの期待値と一致)**、
      subset/full がエラーなくコンパイル (subset 3417 組合せ + 136 DFF、full 8891 + 168)
      * subset smoke 3 周期が GPU トレースと全項目一致 (addr/mem_read/din/pc/a)
      * subset のフェッチ不具合を確定: `LD A,0x42` の後 addr=0x0101 のまま NOP を読み続ける
- [x] B-2 `src/Testbench.fs` + `src/ExportGolden.fsx` (2026-09-16): `--memory` と同じプログラム JSON と
      メモリ契約 (DESIGN-VERIFY.md §5.2) で NetlistSim を回し golden を生成。routed meta と verilog JSON の
      SHA-256 が一致しなければ中止 (終了コード 3)。`routed/sm83_subset_smoke.golden.json` を生成 (expect 2/2)
      * テスト `TestbenchTest` 10 件 (`src/TestbenchTests.fs`): メモリモデル、プログラム読込、バス解決、golden 形式、
        subset smoke が GPU トレースと周期ごとに一致 (data_in/pc/a)、**sm83_full が SM83 仕様どおり動く**
      * GPU トレースの `addr` は clk=0 settle 時点、golden の出力は clk=1 settle 後。時点が違うので比べない
      * E2eTests.fs に置くと最上位の値の初期化 (alu4 配線など) に巻き込まれ単独実行が 160 秒 → 別ファイルで 0.6 秒
- [x] sm83_full.v の即値読出 off-by-one を修正 (2026-09-16、B-2 の仕様テストで発見)。
      `exec_normal` / `exec_imm` の `addr <= pc` 43 か所 → `addr <= pc + 1`。再合成で 9,059 → 9,155 gates。
      合成手順は SM83.md に記録
- [x] B-3b `--memory` に golden 照合 (2026-09-16): プログラムの `"golden"` で有効化。
      起動時に形式・回路名・`sourceSha256` (meta)・`romSha256` (実 ROM から計算)・rstPulses・周期数・出力ポート集合を検査。
      周期ごとに `data_in` と clk=1 settle 後の全出力 (観測不能ビット除外) を比べ、最初の食い違いで停止し
      ポート・期待値/実測値・食い違ったビット位置を表示、`--dump-dir` でその周期の setup/high グリッドを保存。
      停止時は最終状態の expect 照合を飛ばす。`cargo test` 13 件
      * sm83_subset smoke: golden 3/3 周期一致 (RTX 3060)
      * **検証器の検証**: a_out[6] の DFF の D 入力セルを空にした .bin → cycle 1 で
        `a_out: expected 0x42 got 0x2 (bits [6])` と壊したビットだけを指摘して停止
      * `romSha256` を書き換えた golden は GPU 実行前にエラー
- [ ] 決定待ち: プログラム・ROM を `routed/` から `programs/` に分けるか (DESIGN-VERIFY.md §8 Q2、未解決。冒頭「残課題」10 参照)
- [ ] B-5 RTL との照合 (`yosys sim -vcd`)。B-4 は Step C (未実施。冒頭「残課題」11 参照。代わりに公開テスト ROM + gbfs 差分で代替確認済み)
- [x] B-6 GPU 収束判定の高速化 (2026-09-27 完了、PR #5)。シェーダーが世代ごとの変化タイル数を書き、
      ホストはそれだけを読み戻す方式に変更 (旧: 判定のたびにグリッド全体を読み戻していた)
- [x] 決定 (2026-09-15): sm83_subset の RTL フェッチ不具合 (FETCH 後に `addr_r` を更新しない) は
      直さない。subset は CA とネットリストの一致検証専用とし、CPU としての意味の検証は full で行う
      (DESIGN-VERIFY.md §8 Q1)

### Step C: sm83_subset で全工程を通す ✅ (2026-09-17、B-4)

- [x] sm83_subset を配線 (111.6 分、16x12 失敗 → 20x14、2026-09-16) → `routed/sm83_subset.{bin,meta.json}`
- [x] 配線済み CA とネットリストの全周期照合 (`wgpu-runner/memory-test.sh`、RTX 3060):
      * `sm83_subset_smoke` (LD A,0x42): golden 3/3、expect 2/2
      * `sm83_subset_call_stack` (CALL 0x0100、RAM 0xF000-0xFFFF): golden **64/64**。
        SP デクリメント・スタック書込と読み返し・pc 0x01CD/0x0101 の往復を通る
      * `sm83_subset_pc_carry` (LD A,0x5A + NOP): golden **300/300**。pc 0x0101 → 0x022B で下位→上位バイトの桁上がりを通す
      * 食い違いなし: skew 46 のクロック木でも、この範囲では hold 違反は出ていない
      * プログラムは NetlistSim で候補を動かし、出力ビットの変化量で選んだ (JP/JR ループや INC は動く範囲が狭い)
- 注意: subset はフェッチ不具合のため普通の命令列を実行できない。LD/ALU/INC/DEC の網羅的な命令検証は full で行う
  (subset の `INC r` / `DEC r` は `opcode[5:3]` ではなく `opcode[2:0]` でレジスタを選ぶ不具合もある。直さない)

### Step D: sm83_full

- [x] 仕様テスト (2026-09-17): `routed/sm83_full_*.json` 16 本を TestbenchTest で NetlistSim 上で照合。
      期待値は SM83 仕様から手で導出。配線 (2026-09-17 21:25 開始) 中にこのテストで RTL 不具合 7 件が見つかり、
      配線を中止して修正 (詳細 SM83.md)。9,155 → 9,958 gates
- [x] gbfs の CPU との差分テスト `src/DiffTestGbfs.fsx` (2026-09-17): 494 命令 × 4 パターン (境界値入り)。
      1 回目 52 命令食い違い → すべて RTL の誤り (JR、条件分岐の不成立、ADD HL/ADD SP のフラグ、LD (nn),SP、CB (HL))。
      修正後 494 命令すべて一致。代表 12 件を `routed/sm83_full_diff_*.json` に書き出し TestbenchTest の回帰テストへ。
      gbfs 側の ADD の Z フラグ不具合も修正 (gbfs のテスト 222/222)。10,650 gates
- [x] 割込みの実装 (2026-09-17): IE/IF は CPU の外、ポート irq[4:0] / int_ack[4:0]。EI の 1 命令遅延、DI、RETI、HALT 復帰。
      Testbench.fs と wgpu-runner のメモリモデルに IF/IE/HRAM と割込み手順を追加 (DESIGN-VERIFY.md §5.2〜5.3.1)。
      手書き仕様テスト 3 本、gbfs 差分に LDH と割込みシナリオを追加。10,654 gates
- [x] gbfs の EI を 1 命令遅延に修正 (2026-09-17、gbfs の CpuState.ImeScheduled)。修正後、gbfs は仕様テスト 31/31、
      割込みシナリオ 200 本すべてが RTL (NetlistSim) と一致
- [x] HALT バグを RTL と gbfs に実装 (2026-09-17、Pan Docs 準拠)。仕様テスト 2 本追加、int_halt_wake_di の期待値を修正。
      gbfs 参照モデル 33/33、割込みシナリオ 200 本一致。10,767 gates
- [ ] STOP (ジョイパッド入力線で復帰。周辺回路の仕様を決めてから。GB ソフトでの使用は稀)
- [x] 通常命令「全 256」の網羅確認 — 上記差分テストで未定義 opcode・STOP・LDH を除き網羅

- [x] A* の探索爆発を解消 (2026-09-18): 同じ f なら h の小さい状態を先に見る同点処理を入れた。
      序盤のほぼ空なグリッドでは同コスト経路が大量にあり、素の f だけだと平地を一様に広げて爆発していた
      (24x16 のクロック 1 終端で 738 秒・探索上限 500 万到達、全体 36 時間見込み)。
      → クロック 1 終端 55 秒、181 本で約 60 秒、全体 93 分。メモリ 7GB → 1.35GB。探索上限も 500 万 → 200 万
- [x] 配線の進行ログを追加 (2026-09-18): クロック配線の本数・進捗・遅い終端、スキュー均等化のネットごとの時間、
      データ配線の開始と遅い終端、A* の探索拡大 (どのネットで膨らんだか)。従来はデータ配線 100 端子ごとしか出ず、
      クロック配線で 5 時間無反応でも原因が分からなかった
- [x] sm83_full (最終 10,767→10,859 combinational + 181 DFF、2 相化で DFF 362・gateCount 11,221) の
      配線完走 ✅ (2026-09-27、PR #5)。**アニーリング配置 (`--place anneal`) + 20x14 ピッチで 16.1 分、rip-up 0**。
      試行と結果 (経緯):
      | 日付 | 条件 | 到達 | 結果 |
      |---|---|---|---|
      | 09-18 | 16x12 (行優先) | 約 5,800 (32%) | 輻輳失敗 (72 分) |
      | 09-18 | 20x14 (行優先) | 14,600 (81%) | 輻輳失敗 (90 分) |
      | 09-18 | 24x16 (行優先) | 15,153 (84%) | 輻輳失敗 (93.6 分、NetId 165) |
      | 09-23 | 28x20 (案 A: ピッチ拡大) | 16,000 (89%) | 輻輳失敗 (142.9 分、NetId 271) |
      | 09-23 | 28x20 + 撤去上限 80本/8回/5000 (案 B: 撤去強化) | 16,016 (89%) | 輻輳失敗 (142.0 分、**同じ NetId 271**) |
      | 09-24 | 28x20 ヒルベルト (案 D 派生) | 13,600 (75%) | 打ち切り (39 分、rip-up 0。遅い終端 20 秒超で失速) |
      | 09-27 | 32x24 (行優先) | — | 打ち切り／未収束 (`sm83_full_p32x24.log` 4,411 秒で 18,691 件中 13,600 台、`sm83_full_32x24.log` も同様に遅延) |
      | **09-27** | **20x14 + アニーリング配置 + 2 相クロック** | **18,691/18,691 (100%)** | **✅ 完走 (16.1 分、rip-up 0)** |
      * 案 A (ピッチ拡大): 84% → 89% と伸びたが頭打ち。グリッドが広がる分 1 端子あたりの経路も伸びる
      * 案 B (撤去強化): 撤去 123 → 240 回に増えたのに到達点は +16 端子、失敗ネットも同じ。
        「何本どけても通り道が無い」状態で、撤去回数は律速ではないと確定
      * 案 C (ネット構造の調査、2026-09-23): 最大ファンアウトは 181 (クロック) と 173 のみ、
        93.8% がファンアウト 3 以下。宣言順で前後 2 分割した cut は 1,031 (無作為分割 6,054 の 1/6) で
        yosys の宣言順には強い局所性がある。問題は並び順ではなく格子への敷き方 (行優先 104x104 では、
        宣言順で半行ぶん離れただけで平面上は約 1,450 セル離れる)
      * ヒルベルト配置 (方式比較で推定総配線長は行優先 1,390 万→ヒルベルト 1,297 万まで改善) は
        **クロックスキュー均等化が構造的に効かなくなる**問題 (reg8 で skew 36、本来 ≤1) が未解決のまま
        頭打ちとなり不採用。`placement-hilbert` ブランチに負の結果として記録 (残課題 9)
      * 案 D (密度の定量化、2026-09-24): 面積比 (需要/bbox) と完走に必要な拡張率の相関を実測
        (subset 20x14 で 1.427/1.366x = 完走、full 20x14 で 2.320/2.41x = 輻輳失敗 81% など)。
        32x24 は必要拡張率 1.34x で「次に試す条件」だったが、実際に行優先で試すと長時間終わらず
        (上表 09-27 行)、**ピッチ拡大だけでは解決しないことが確定**
      * → 決定打は面積・撤去・並び順ではなく**配置アルゴリズムそのもの**だった。アーク距離
        (駆動元→受け手のマンハッタン距離の総和) を最小化するシミュレーテッドアニーリング
        (`src/GatePlacement.fs`) に切り替えたところ、総アーク距離が約 22% 減り、1,000 セル超のネットが
        21% → 4% に減少。20x14 のまま **16.1 分・rip-up 0 で完走**
- [x] CB 命令の動作検証 — `DiffTestGbfs.fsx` の全 498 命令 × 4 パターン一致 (CB プレフィックス込み) と、
      仕様テスト `sm83_full_cb_rlc_bit` の GPU 全周期照合で確認済み (2026-09-27)

### 並行して進められる改善 (必須ではない)

- [x] 配線時間の短縮 — sm83_full はアニーリング配置への切替で 142 分 (28x20 輻輳失敗) から
      16.1 分 (20x14 完走) に短縮 (2026-09-27)。ネット単位の並列化は未着手のまま
- [ ] sm83_min のクロックのずれを `verify_clock.fsx` で測り直す
      (2026-09-15 の ExportRouted 実行で `ClockSkewUnresolved (NetId 2, 92)` — 旧記録 110 から改善したが未解消)
- [ ] web/sm83_mc_*.bin の再生成 (P3 参照。冒頭「残課題」8 と同一項目)

最終目標: ゲームボーイエミュレータに組込める CPU をセルオートマトンで実現する。

---

## P0: パイプラインの WireLevel 化 ✅ 完了 (2026-06-11, PipelineWL.fs)

- [x] compileWL: yosys JSON → LGrid (techMap は 1 セルゲートなので compileWL 内で完結)
- [x] placeWL: 正方格子配置 (pitchX=24, pitchY=16 — alu4 輻輳対策で拡大) + 左端ピン列
- [x] routeWL: (Coord,Dir) 状態 A*、Cross 化直交通過、ゲート隣接クリアランス、
      ファンアウトタップ (タップ元は非交差化)
- [x] クロック配線 (通常ネットとして DFF S 側面へ。均等化は未実装 → P1 残課題)
- [x] emitWL: LGrid 合成 (byte 一括エクスポートは P2 で)
- [x] E2E: 半加算器真理値表 4/4

## P1: 順序回路 E2E

- [x] yosys $_DFF_P_ → LDff 経路の E2E (toggle FF、q=1,0,1,0)
- [x] 4bit カウンタ (verilog/counter4.v → yosys → 21 ゲート → 0..15 ラップ確認)
- [x] 8bit レジスタ
- [x] ALU (2bit ADD/AND/OR/XOR, 14 tests)
- [x] ALU 4bit (verilog/alu4.v → yosys → 85 ゲート, 13 tests)。
      初回は RoutingCongestion で失敗 → A* に転回ペナルティ (+4) を導入し
      経路を直線化 (直線セルのみ交差可のため後続ネットの交差点が増える)、
      pitchX=24 / pitchY=16 に拡大して解消
- [x] クロックスキュー均等化 (hold 対策, 2026-06-11)。counter4/reg8 で skew=0 達成。
      実装 (PipelineWL.routeWL 内 balanceClockNet):
      * DFF クロック終端をタップ禁止に (数珠つなぎ分配だと均等化が原理的に不可能)
      * 各終端の専有サフィックス (リーフ edge) のみ延長 → 木の再帰均等化が不要
      * 延長は (1) 直線 run のコの字バンプ (+2h)、足りなければ
        (2) リーフ edge を撤去して幹の任意点から「到達 = tMax」の正確長 DFS で再配線
        (スラック消費優先の方向順序 + パリティ/残距離枝刈り + 自己重複禁止)
      * 経路長のパリティは端点で固定のため、tMax / tMax-1 の両方を試す (残差 ≤1)
      * 検証: WireLevel.clockArrivals (終端からの逆走でパス長 = 到達世代を実測)

### 学んだ設計則

- **半周期 > 組合せ収束時間** (setup 制約)。counter4 は halfP=128 で誤動作、
  512 で完動。テストは固定周期でなく `settle` (収束待ち) でクロックを駆動する。
- **P0 compile では maxExplore=2M が必要**。300k では 264k cells の端-to-端経路が探索不足。
  2M で全経路確保。既存テスト (150/150) への影響なし。
- **P0 settle は ~2500 gen 必要** (cyc0-high が limit hit, cyc0-low は 2161 gen で収束)。
  F# 実装は ~200s/settle と低速 → GPU 検証に委ねる。

## P2: GPU 実行 (WebGPU)

- [x] web/: WGSL compute カーネル (DESIGN-CA2.md §4.3) + ping-pong バッファ
- [x] F# → grid.bin エクスポート / JS ローダー
- [x] ゴールデンテスト: F# WireLevel.step の .bin 入出力自己無矛盾
- [x] 可視化 (canvas カラーマップ描画)
- [x] GPU ゴールデンテスト (2026-06-11, `web/run-test.sh` で 2/2 PASS)。
      未収束 init (ピン設定直後) → GPU N 世代 → F# settle 結果とバイト一致。
      * Playwright はヘッドレスでは SwiftShader adapter。`--enable-unsafe-webgpu`
        が必須 (旧 `--enable-webgpu` は実在しないスイッチで、テストは skip していた)
      * headless-shell ビルドは WebGPU 非対応 → `channel: 'chromium'` を使う
      * Pop!_OS 等ではシステムライブラリで動く。NixOS では flake.nix の
        WWC_CHROMIUM_LIBS を run-test.sh が LD_LIBRARY_PATH に注入
      * 正式ランナーは Playwright (web/run-test.sh)。旧マシン (Vivaldi/NixOS)
        前提だった golden-test-puppeteer.mjs は削除済み

## P3: CPU へ

- [x] SM83 (LR35902) サブセットの Verilog 記述 → yosys 合成 (sm83_min.json, 380 gates)
- [x] P0 拡張 (1095 gates): LD r,#imm8 / MOV r1,r2 / ALU op,r / INC/DEC r / NOP。yosys 合成確認
- [x] P0 compileWL 成功: 264,705 cells, pitch 24×16, maxExplore=2M
- [x] GPU golden test: SM83 P0 10 ケース PASS (cyc0 2 + NOP/LDA/LDB/ADD multi-cycle 8)
- [x] WireLevel CPU のマイクロベンチ (src/TestSm83.fsx, NOP/LD/ALU 8命令) — F# 実装は低速すぎるため GPU 検証で代替
- [x] GPU での SM83 動作確認 (web/ golden test 16/16 PASS, うち SM83/SM83P0 12 ケース)
- [x] 命令レベル GPU 検証 (Phase 1b, 2026-07-10)。F# リファレンス不要で GPU 単独の
      マルチ命令実行 + レジスタ値検証が可能に:
      * wgpu-runner プログラムモード (`--program prog.json [--dump-regs] [--dump-dir D]`)。
        meta JSON (ピン/レジスタの正規化座標バス) + program JSON (命令列 + 期待値) 駆動で
        回路非依存。ループ: pins 書込 → clk=0 固定点 → clk=1 固定点 → レジスタ読出 → 比較。
        固定点検出は F# settle と同値 (interval 実行 → +1 世代不変チェック)
      * `src/ExportSm83MinInstr.fsx`: Sm83MinModel (Verilog quirk 写像。ADD の H は
        4bit ラップ比較 `((a&15)+(b&15))&15 >= 8` に注意) + 正規化 meta + init.bin +
        期待値つき 20 命令 program JSON を生成
      * `wgpu-runner/sm83-instr-test.sh`: 統合ランナー (成果物なければ自動エクスポート)
      * sm83_min 20 命令 (全 opcode + 全フラグ Z/N/H/C、キャリー連鎖/ボロー) 20/20 PASS。
        2 回実行で世代数まで決定的
      * **クロック配線バグを発見・修正**: balanceClockNet の graceful degradation が
        ripUpEdge でリーフ edge を撤去した後 routeExactLen 失敗時に復元せず、
        DFF b[1] がクロック未接続 → レジスタビットがリセット値に固着していた
        (PipelineWL.fs: 撤去前の occ を退避し失敗時に復元)。従来の golden テストは
        b bit1=1 を通る値を一度もロードしていなかったため検出できなかった
- [x] 大規模回路の配線 (2026-06-15〜08-14)。sm83_subset (3,553 gates) 配線完走:
      * ルーティング順序最適化 (短いネット優先) + A* リトライ時の bbox マージン拡大 (bc358f0)
      * リトライ時の転回ペナルティ低減 4→2→1 (a2b64f5)
      * Rip-up & reroute: 輻輳ネットの経路を実際に塞いだ「ブロッカー」を撤去して
        再配線、失敗時は occ 復元 (679f792, 6cc7704)
      * A* 探索上限 50M → 5M: 経路なしネットを早く諦めて rip-up に回す (dc5c367)
      * 配置ピッチの動的決定 `pitchFor` (≤200: 24x16 / ≤1000: 20x14 / ≤3000: 16x12 /
        >3000: 16x12) と輻輳失敗時の自動拡大 `pitchSequence` 12x10→16x12→20x14→24x16
        (0c3e39b, 0fe4566)。sm83_subset は 16x12 で失敗 → 20x14 で完走
        * 2026-09-28 追記: この閾値は**行優先配置時代**の測定に基づく。タイミング駆動配置なら
          sm83_full は 14x12 で完走する (12x10 は失敗)。ピッチは 1 周期の直接のスケール因子
      * クロック優先配線: クロック終端を先に配線・均等化してからデータ配線 (12de255)。
        sm83_subset skew 1068 → 46
      * 検証スクリプト: verify_clock.fsx (skew 実測), pitch_bench.fsx / pitch_route.fsx
- [x] sm83_full に CB prefix (0xCB) デコード追加 (d008cbe)。8,379 → 9,059 gates、yosys 合成成功
- [ ] web/sm83_mc_*.bin (2026-06-13 生成) の再生成 — クロック未接続バグ入り
      回路のもの (F#/GPU 一致テストとしては有効だが回路として b[1] 欠陥あり)
- [ ] GB エミュレータ統合 (バス/割込みブリッジ)

### 開発サイクルへの GPU 統合

`web/run-wl.sh` がコンパイル → .bin エクスポート → GPU シミュレーション → 検証
を一括実行する:

```bash
web/run-wl.sh sm83          # SM83: export → GPU test
web/run-wl.sh --all         # 全回路: export → GPU golden test (16 ケース, ~4min)
web/run-wl.sh --list        # 利用可能テスト一覧
web/run-wl.sh mincpu --headed  # ブラウザ表示あり
```

アーキテクチャ:
- `web/golden-cases.json` — テストケース定義 (init/steps/expected/exportScript)
- `golden-test.ts` — JSON 駆動の Playwright テスト (.bin 不在時は自動スキップ + エクスポートヒント表示)
- `run-wl.sh` — 統合ランナー (依存自動セットアップ付き, npm/playwright install 不要)

---

## 既知の問題

- 大規模回路の配線時間: sm83_subset は約 100〜120 分 (20x14、行優先)。sm83_full は行優先だと
  5〜8 時間見込み (実際には輻輳失敗して完走しなかった) だったが、**アニーリング配置に切り替えると
  16.1 分に短縮** (2026-09-27)。行優先のまま使う既存回路 (subset 等) では引き続き遅い
- ピッチ拡大に伴い cross 率が上昇 (12x10 で約 22%、sm83_subset 20x14 で約 31%)
- クロックスキュー: クロック優先配線 (12de255) で大幅改善 (sm83_subset skew 46)。
  sm83_min の旧 WARN (残差 110 gen) は優先配線後に未再計測 → verify_clock.fsx で要確認
- sm83_full の CA 実行時間は 1 周期の約 77% が組合せ論理の収束待ち (クロック木は理論下限に到達済みのため、
  次の高速化はここが対象。冒頭「残課題」1 参照)

### 解消済み

- `verilog/sm83_p0.json` / `sm83_p0.v` の紛失 → 復元済み (2674330)
- 399b9c8 に混入した PipelineWL.fs 高速化 WIP (trySimplePath) → trySimplePath は
  配線資源を食い RoutingCongestion を起こすため削除 (2674330)

---

## WireWorld 系 (凍結 — 組合せ回路デモとして維持)

WireWorld 系テストは構造的制約により修正しない。現在 90 テストが WireWorld 系
(内訳は PR #5 時点で未再計測)。全テストは 421/421 PASS (2026-09-28 実測、クロックピン分割ブランチ)。
