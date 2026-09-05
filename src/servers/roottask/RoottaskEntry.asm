default rel
section .text

global RoottaskEntry
extern RoottaskMain

global Syscall
global GetCs

; -----------------------------------------------------------------------------
; Roottask Flat Binary Entry Point (at .text RVA 0x1000 -> virtual 0x40001000)
; -----------------------------------------------------------------------------
RoottaskEntry:
    ; 16-byte align user stack and allocate 32-byte shadow space for Microsoft x64
    and rsp, -16
    sub rsp, 32
    call RoottaskMain
    add rsp, 32

.halt:
    hlt
    jmp .halt

; -----------------------------------------------------------------------------
; Hardware Syscall Wrapper
; ulong Syscall(ulong num, ulong a1, ulong a2, ulong a3, ulong a4, ulong a5, ulong a6)
; Input (Microsoft x64 ABI):
; rcx = num, rdx = a1, r8 = a2, r9 = a3, [rsp + 40] = a4, [rsp + 48] = a5, [rsp + 56] = a6
; Syscall registers:
; rax = num, rdi = a1, rsi = a2, rdx = a3, r10 = a4, r12 = a5, r13 = a6
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
; ulong GetCs()
; Returns current CS selector
; -----------------------------------------------------------------------------
GetCs:
    mov ax, cs
    movzx rax, ax
    ret

; -----------------------------------------------------------------------------
; ulong GetRsp()
; Returns current user stack pointer
; -----------------------------------------------------------------------------
global GetRsp
GetRsp:
    mov rax, rsp
    ret

; -----------------------------------------------------------------------------
; void CaptureCalleeSavedRegisters(ulong* outRegisters)
; Captures rbx, rbp, r12, r13, r14, r15 into [rcx] for GC root scanning
; -----------------------------------------------------------------------------
global CaptureCalleeSavedRegisters
CaptureCalleeSavedRegisters:
    mov [rcx + 0],  rbx
    mov [rcx + 8],  rbp
    mov [rcx + 16], r12
    mov [rcx + 24], r13
    mov [rcx + 32], r14
    mov [rcx + 40], r15
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
