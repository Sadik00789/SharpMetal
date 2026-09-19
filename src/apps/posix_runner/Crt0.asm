default rel
section .text

global PosixRunnerEntry
extern PosixRunnerMain

global JumpToPie
global Syscall

; -----------------------------------------------------------------------------
; PosixRunner Flat Binary Entry Point
; -----------------------------------------------------------------------------
PosixRunnerEntry:
    and rsp, -16
    sub rsp, 32
    call PosixRunnerMain
    add rsp, 32

.halt:
    mov rcx, 1          ; SysYield
    call Syscall
    jmp .halt

; -----------------------------------------------------------------------------
; System V AMD64 Trampoline to PIE Entry Point
; void JumpToPie(ulong entryRip, ulong userRsp)
; Input (Microsoft x64 ABI):
;   rcx = entryRip
;   rdx = userRsp (System V stack pointer pointing to argc)
; -----------------------------------------------------------------------------
JumpToPie:
    mov r12, rcx        ; preserve entryRip in callee-saved r12
    mov rsp, rdx        ; switch stack to System V initial stack

    ; 1. Issue SysSetAbi(1) to transition calling thread to Linux ABI mode
    mov rax, 0x26       ; SyscallNumbers.SysSetAbi
    mov rdi, 1          ; a1 = 1 (Linux ABI)
    syscall

    ; 2. Clear GPRs per System V AMD64 ABI specification
    ; rdx = 0 indicates no registered shared-library termination function (atexit)
    xor rax, rax
    xor rbx, rbx
    xor rcx, rcx
    xor rdx, rdx
    xor rsi, rsi
    xor rdi, rdi
    xor rbp, rbp
    xor r8, r8
    xor r9, r9
    xor r10, r10
    xor r11, r11
    xor r14, r14
    xor r15, r15

    mov r13, r12
    xor r12, r12

    ; 3. Jump to the PIE entry point in Ring 3
    jmp r13

; -----------------------------------------------------------------------------
; Hardware Syscall Wrapper (Microsoft x64 ABI -> Hardware SYSCALL)
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
global RhpStelemRef
global RhpNewArray
global RhpNewArrayFast

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

RhpStelemRef:
    mov [rcx], rdx
    ret

RhpNewFast:
    lea rax, [rel .static_obj_buf]
    ret
.static_obj_buf: times 256 db 0

RhpNewArrayFast:
RhpNewArray:
    push rbx
    push r12
    push r13
    push rdi
    mov rbx, rcx        ; MethodTable
    mov r12, rdx        ; length

    ; calculate size: 16 (header) + length * 8 + 15, aligned to 16
    mov rax, rdx
    shl rax, 3
    add rax, 31
    and rax, -16
    mov r13, rax        ; allocation size

    ; Bump pointer allocation
    lea rcx, [rel array_bump_ptr]
    mov rax, [rcx]
    test rax, rax
    jnz .allocated
    lea rax, [rel array_heap_buf]
.allocated:
    lea rdx, [rax + r13]
    mov [rcx], rdx

    ; Zero out the allocated memory
    mov rdi, rax
    mov rcx, r13
    shr rcx, 3
    xor edx, edx
.zero_loop:
    test rcx, rcx
    jz .zero_done
    mov [rdi], rdx
    add rdi, 8
    dec rcx
    jmp .zero_loop
.zero_done:

    ; Set MethodTable and Length
    mov [rax], rbx
    mov [rax + 8], r12d

    pop rdi
    pop r13
    pop r12
    pop rbx
    ret

RhpInitialDynamicInterfaceDispatch:
    ret

section .bss
align 16
array_bump_ptr: resq 1
array_heap_buf: resb 262144

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232

