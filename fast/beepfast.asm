; BEEPFAST: ROM をブザーの音で高速に出す。BEEPFAST.TXT が POKE して CALL する。
;
; 符号はバイフェーズ: 各ビットの頭で必ず出力を反転し、ビットが 1 なら中央でも反転する。下位ビットから送る。
; 1 のビットは 0 のビットより少し長い（処理の分岐の差）。PC 側はこの差も学習して読む。
; 最初に 1 回、学習用の並び（無音、$00、$00、$5A、この機械語の先頭 128 バイト）を出す。PC 側はこれでブザーの応答を覚える。
; データと同じ処理で送るので、ビットの時間も同じになる。
; 続いてブロック（256 バイト）ごとに:
;   無音、$00、$00、$A5、データ 256 バイト、S1（3 バイト）、S2（3 バイト）。どちらも下位が先で 20 bit
; S1 はブロックの番地とデータの和、S2 は S1 の累積和。番地は送らず、S1 から求める。
; ブロックの切れ目で ON キーを見て、押されていれば BASIC に戻る。

        ORG   $BFA00

PARAM   EQU   $BFBE0
START   EQU   PARAM            ; 開始番地
LAST    EQU   PARAM+3          ; 最後のブロックの次の番地
DELAY   EQU   PARAM+6          ; 半ビットの待ち（サイクル）。大きいほど遅い
SAVEW   EQU   PARAM+8

SCR     EQU   $FD
SSR     EQU   $FF
BITS    EQU   0                ; (BP+BITS): 残りのビット数
COUNT   EQU   1                ; (BP+COUNT): 残りのバイト数

        PUSHU IMR              ; 割込み禁止（音の間隔を揃える）
        MV    [--S],U
        MVW   [SAVEW],(BP+BITS)
        MV    X,$BFA00         ; 学習用の並び
        MV    (BP+COUNT),128
        MV    A,$5A
        CALL  head
        CALL  data
        MV    X,[START]
block:  TEST  (SSR),$08        ; ON キーを押している間。ISR のビット 3 は前に押した記録が残っていることがある（実機の PC-U6000）
        JRNZ  done
        MV    A,$A5
        CALL  head
        MV    Y,X              ; S1 の初期値は番地
        SUB   U,U              ; S2
        MV    (BP+COUNT),0
        CALL  data
        MV    [--S],U          ; S1、S2 の順に下位から取り出せる
        MV    [--S],Y
        MV    (BP+COUNT),6
check:  MV    A,[S++]
        CALL  send
        DEC   (BP+COUNT)
        JRNZ  check
        MV    Y,[LAST]
        SUB   Y,X              ; 番地は 20 bit で一周するので、大小でなく一致で終わりを見る
        JRNZ  block
done:   AND   (SCR),$8F
        MVW   (BP+BITS),[SAVEW]
        MV    U,[S++]
        POPU  IMR
        RC
        RETF

; X から (BP+COUNT) バイトを S1・S2 に足しながら送る
data:   MV    A,[X++]
        ADD   Y,A
        ADD   U,Y
        CALL  send
        DEC   (BP+COUNT)
        JRNZ  data
        RET

; 無音のあと、$00、$00、同期（A）
head:   AND   (SCR),$8F
        MV    B,A
        MV    I,8000
        WAIT
        MV    A,0
        RC
        CALL  send
        CALL  send             ; A は 0 のまま
        MV    A,B

; A を 8 ビット送る
send:   MV    (BP+BITS),8
sbit:   XOR   (SCR),$10
        CALL  wait
        SHR   A                ; C = 次のビット
        JRNC  next
        XOR   (SCR),$10
next:   CALL  wait
        DEC   (BP+BITS)
        JRNZ  sbit
        RET

wait:   MV    I,[DELAY]
        WAIT
        RET

        END
