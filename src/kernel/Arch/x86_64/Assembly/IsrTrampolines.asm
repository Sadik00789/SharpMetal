default rel
section .text

extern DispatchInterrupt

; -----------------------------------------------------------------------------
; Common ISR Dispatch Wrapper
; Stack layout upon entry to isr_common_stub:
; [rsp + 0]  = Vector Number (8 bytes)
; [rsp + 8]  = Error Code (8 bytes)
; [rsp + 16] = RIP (8 bytes)
; [rsp + 24] = CS (8 bytes)
; [rsp + 32] = RFLAGS (8 bytes)
; [rsp + 40] = RSP (8 bytes)
; [rsp + 48] = SS (8 bytes)
; Total = 56 bytes.
; -----------------------------------------------------------------------------
isr_common_stub:
    ; 15 GPRs pushed here: rax, rcx, rdx, rbx, rbp, rsi, rdi, r8..r15
    ; Stack is currently 16-byte aligned (0 mod 16: 56 + 120 = 176 bytes)
    push r15
    push r14
    push r13
    push r12
    push r11
    push r10
    push r9
    push r8
    push rdi
    push rsi
    push rbp
    push rbx
    push rdx
    push rcx
    push rax

    mov rcx, rsp       ; 1st parameter: InterruptContext*

    ; Ensure 16-byte stack alignment prior to calling C# DispatchInterrupt (prevents #GP on SIMD)
    test rsp, 8
    jz .aligned
    sub rsp, 8         ; Align stack to 16 bytes
    sub rsp, 32        ; Allocate 32-byte shadow space
    call DispatchInterrupt
    add rsp, 40        ; Restore shadow space (32) + alignment adjustment (8)
    jmp .done_dispatch
.aligned:
    sub rsp, 32        ; Allocate 32-byte shadow space
    call DispatchInterrupt
    add rsp, 32        ; Restore shadow space
.done_dispatch:

    ; Restore 15 GPRs in reverse order
    pop rax
    pop rcx
    pop rdx
    pop rbx
    pop rbp
    pop rsi
    pop rdi
    pop r8
    pop r9
    pop r10
    pop r11
    pop r12
    pop r13
    pop r14
    pop r15

    add rsp, 16        ; Discard vector number and error code
    iretq

; -----------------------------------------------------------------------------
; Macro definitions for ISR entry points
; -----------------------------------------------------------------------------
%macro ISR_NOERR 1
isr_stub_%1:
    push qword 0          ; Dummy error code
    push qword %1         ; Vector number
    jmp isr_common_stub
%endmacro

%macro ISR_ERR 1
isr_stub_%1:
    ; Error code already pushed by CPU
    push qword %1         ; Vector number
    jmp isr_common_stub
%endmacro

; -----------------------------------------------------------------------------
; ISR entry points 0..255
; -----------------------------------------------------------------------------
ISR_NOERR 0
ISR_NOERR 1
ISR_NOERR 2
ISR_NOERR 3
ISR_NOERR 4
ISR_NOERR 5
ISR_NOERR 6
ISR_NOERR 7
ISR_ERR   8
ISR_NOERR 9
ISR_ERR   10
ISR_ERR   11
ISR_ERR   12
ISR_ERR   13
ISR_ERR   14
ISR_NOERR 15
ISR_NOERR 16
ISR_ERR   17
ISR_NOERR 18
ISR_NOERR 19
ISR_NOERR 20
ISR_ERR   21
ISR_NOERR 22
ISR_NOERR 23
ISR_NOERR 24
ISR_NOERR 25
ISR_NOERR 26
ISR_NOERR 27
ISR_NOERR 28
ISR_ERR   29
ISR_ERR   30
ISR_NOERR 31

; User and hardware interrupt vectors 32..255 (none push error codes)
%assign i 32
%rep 224
    ISR_NOERR i
%assign i i+1
%endrep

; -----------------------------------------------------------------------------
; Function to retrieve the ISR thunk table pointer
; -----------------------------------------------------------------------------
global GetIsrThunkTable
GetIsrThunkTable:
    lea rax, [rel isr_thunk_table]
    ret

; -----------------------------------------------------------------------------
; 256-entry table of ISR stub function pointers
; -----------------------------------------------------------------------------
section .data
global isr_thunk_table
isr_thunk_table:
%assign i 0
%rep 256
    dq isr_stub_%+i
%assign i i+1
%endrep
