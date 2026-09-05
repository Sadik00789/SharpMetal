default rel
section .text

global DisplayServerEntry
extern DisplayServerMain

global Syscall
global Avx2Blit
global Avx2Fill

; -----------------------------------------------------------------------------
; DisplayServer Flat Binary Entry Point
; -----------------------------------------------------------------------------
DisplayServerEntry:
    and rsp, -16
    sub rsp, 32
    call DisplayServerMain
    add rsp, 32

.halt:
    hlt
    jmp .halt

; -----------------------------------------------------------------------------
; Hardware Syscall Wrapper
; ulong Syscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
; -----------------------------------------------------------------------------
Syscall:
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15

    mov rax, rcx            ; num
    mov rdi, rdx            ; a1
    mov rsi, r8             ; a2
    mov rdx, r9             ; a3

    ; 6 pushes (48 bytes) + return address (8 bytes) + 32-byte shadow space = 88 bytes
    mov r10, [rsp + 88]     ; a4
    mov r12, [rsp + 96]     ; a5
    mov r13, [rsp + 104]    ; a6

    syscall

    pop r15
    pop r14
    pop r13
    pop r12
    pop rbp
    pop rbx
    ret

; -----------------------------------------------------------------------------
; AVX2 256-Bit Blitter Routines (Strictly vmovdqu to avoid #GP alignment faults)
; -----------------------------------------------------------------------------

; void Avx2Blit(void* dst, void* src, ulong byteCount)
; rcx = dst, rdx = src, r8 = byteCount
Avx2Blit:
    push rdi
    push rsi
    mov rdi, rcx
    mov rsi, rdx
    mov rcx, r8

.blit32:
    cmp rcx, 32
    jb .blit1
    vmovdqu ymm0, [rsi]
    vmovdqu [rdi], ymm0
    add rsi, 32
    add rdi, 32
    sub rcx, 32
    jmp .blit32

.blit1:
    test rcx, rcx
    jz .blitDone
    mov al, [rsi]
    mov [rdi], al
    inc rsi
    inc rdi
    dec rcx
    jmp .blit1

.blitDone:
    vzeroupper
    pop rsi
    pop rdi
    ret

; void Avx2Fill(void* dst, uint color, ulong pixelCount)
; rcx = dst, edx = color (32-bit ARGB), r8 = pixelCount
Avx2Fill:
    push rdi
    mov rdi, rcx
    vmovd xmm0, edx
    vpbroadcastd ymm0, xmm0
    mov rcx, r8

.fill8:
    cmp rcx, 8
    jb .fill1
    vmovdqu [rdi], ymm0
    add rdi, 32
    sub rcx, 8
    jmp .fill8

.fill1:
    test rcx, rcx
    jz .fillDone
    mov [rdi], edx
    add rdi, 4
    dec rcx
    jmp .fill1

.fillDone:
    vzeroupper
    pop rdi
    ret

; -----------------------------------------------------------------------------
; Freestanding RyuJIT Runtime Stubs
; -----------------------------------------------------------------------------
global RhpReversePInvoke
global RhpReversePInvokeReturn
global RhpPInvoke
global RhpPInvokeReturn
global RhpFallbackFailFast
global RhpThrowEx
global __security_check_cookie
global RhpAssignRef
global RhpNewFast
global RhpInitialDynamicInterfaceDispatch

RhpReversePInvoke: ret
RhpReversePInvokeReturn: ret
RhpPInvoke: ret
RhpPInvokeReturn: ret
RhpFallbackFailFast: ret
RhpThrowEx: ret
__security_check_cookie: ret

RhpAssignRef:
    mov [rcx], rdx
    ret

RhpNewFast:
    lea rax, [rel .static_obj_buf]
    ret
.static_obj_buf: times 256 db 0

RhpInitialDynamicInterfaceDispatch:
    ret

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232
