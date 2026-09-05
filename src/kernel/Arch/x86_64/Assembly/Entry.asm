default rel
section .text

; -----------------------------------------------------------------------------
; RyuJIT Freestanding Reverse P/Invoke & P/Invoke Stubs
; -----------------------------------------------------------------------------
global RhpReversePInvoke
global RhpReversePInvokeReturn
global RhpPInvoke
global RhpPInvokeReturn
global RhpFallbackFailFast
global RhpThrowEx
global __security_check_cookie

RhpReversePInvoke:
    ret

RhpReversePInvokeReturn:
    ret

RhpPInvoke:
    ret

RhpPInvokeReturn:
    ret

__security_check_cookie:
    ret

RhpFallbackFailFast:
    cli
    hlt
    jmp RhpFallbackFailFast

RhpThrowEx:
    cli
    hlt
    jmp RhpThrowEx

section .data
global __security_cookie
__security_cookie:
    dq 0x00002B992DDFA232

section .text

; -----------------------------------------------------------------------------
; Hardware Port I/O Stubs (Microsoft x64 Calling Convention)
; rcx = arg1, rdx = arg2, r8 = arg3, r9 = arg4
; -----------------------------------------------------------------------------
global Out8
global In8
global Out16
global In16
global Out32
global In32
global IoWait

; void Out8(ushort port, byte value)
Out8:
    mov r8w, cx
    mov al, dl
    mov dx, r8w
    out dx, al
    ret

; byte In8(ushort port)
In8:
    mov dx, cx
    in al, dx
    movzx eax, al
    ret

; void Out16(ushort port, ushort value)
Out16:
    mov r8w, cx
    mov ax, dx
    mov dx, r8w
    out dx, ax
    ret

; ushort In16(ushort port)
In16:
    mov dx, cx
    in ax, dx
    movzx eax, ax
    ret

; void Out32(ushort port, uint value)
Out32:
    mov r8w, cx
    mov eax, edx
    mov dx, r8w
    out dx, eax
    ret

; uint In32(ushort port)
In32:
    mov dx, cx
    in eax, dx
    ret

; void IoWait()
IoWait:
    mov al, 0
    out 0x80, al
    ret

; -----------------------------------------------------------------------------
; CPU Control Registers, MSRs, and Interrupt Primitives
; -----------------------------------------------------------------------------
global ReadCr0
global WriteCr0
global ReadCr3
global ReadCr2
global WriteCr3
global ReadCr4
global WriteCr4
global ReadMsr
global WriteMsr
global DisableInterrupts
global EnableInterrupts
global GetRsp
global GetRip

ReadCr0:
    mov rax, cr0
    ret

WriteCr0:
    mov cr0, rcx
    ret

ReadCr3:
    mov rax, cr3
    ret

ReadCr2:
    mov rax, cr2
    ret

WriteCr3:
    mov cr3, rcx
    ret

ReadCr4:
    mov rax, cr4
    ret

WriteCr4:
    mov cr4, rcx
    ret

; ulong ReadMsr(uint msr) - rcx = msr
ReadMsr:
    mov ecx, ecx
    rdmsr
    shl rdx, 32
    or rax, rdx
    ret

; void WriteMsr(uint msr, ulong value) - rcx = msr, rdx = value
WriteMsr:
    mov eax, edx
    shr rdx, 32
    wrmsr
    ret

; void XSetBv(uint ecx, ulong value) - rcx = register index (ecx), rdx = value
global XSetBv
XSetBv:
    mov eax, edx
    shr rdx, 32
    xsetbv
    ret

DisableInterrupts:
    cli
    ret

EnableInterrupts:
    sti
    ret

GetRsp:
    mov rax, rsp
    add rax, 8 ; offset return address
    ret

GetRip:
    lea rax, [rel $]
    ret

; -----------------------------------------------------------------------------
; Higher-Half Transition Trampoline
; void SwitchToHigherHalf(ulong pml4Phys, ulong highRsp, ulong entryPointHigh)
; rcx = pml4Phys
; rdx = highRsp
; r8  = entryPointHigh
; -----------------------------------------------------------------------------
global SwitchToHigherHalf
SwitchToHigherHalf:
    ; 1. Load new CR3 with custom PML4 physical address
    mov cr3, rcx

    ; 2. Ensure 16-byte stack alignment
    and rdx, -16
    mov rsp, rdx

    ; 3. Allocate 40 bytes (32-byte shadow space + 8-byte alignment)
    ; Simulates 'call' state (rsp % 16 == 8) so RyuJIT function prologues succeed
    sub rsp, 40

    ; 4. Jump to higher-half kernel entry point
    jmp r8
