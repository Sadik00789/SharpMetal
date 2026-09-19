default rel
section .text

global VirtioNetEntry
extern VirtioNetMain

global Syscall

; -----------------------------------------------------------------------------
; VirtioNet Flat Binary Entry Point
; -----------------------------------------------------------------------------
VirtioNetEntry:
    and rsp, -16
    sub rsp, 32
    call VirtioNetMain
    add rsp, 32

.halt:
    mov rcx, 1
    call Syscall
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
    ; rcx = MethodTable*
    ; rdx = length
    push rbx
    push r12
    push r13
    push rdi
    mov rbx, rcx        ; MethodTable
    mov r12, rdx        ; length

    ; calculate size: 16 (header) + length * element_size
    movzx rax, word [rcx + 4]   ; Component size
    test rax, rax
    jnz .has_elem_size
    mov rax, 8
.has_elem_size:
    imul rax, rdx
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
array_heap_buf: resb 524288

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232
