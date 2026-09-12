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

global DisableInterrupts
global EnableInterrupts
global Invlpg
global ReadRflags
global RestoreRflags
global GetRsp
global GetRip

Invlpg:
    invlpg [rcx]
    ret

ReadRflags:
    pushfq
    pop rax
    ret

RestoreRflags:
    push rcx
    popfq
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

; -----------------------------------------------------------------------------
; CPUID ECX query (Microsoft x64 ABI: rcx = leaf, rax = ecx return)
; -----------------------------------------------------------------------------
global CpuIdEcx
CpuIdEcx:
    push rbx
    mov eax, ecx
    xor ecx, ecx
    cpuid
    mov eax, ecx
    pop rbx
    ret

; -----------------------------------------------------------------------------
; Infinite Halt (cli; hlt; jmp)
; -----------------------------------------------------------------------------
global Halt
Halt:
    cli
.loop:
    hlt
    jmp .loop

; -----------------------------------------------------------------------------
; Universal Hardware Reset via CPU Triple Fault (lidt [0] + int3)
; -----------------------------------------------------------------------------
global TripleFaultReset
TripleFaultReset:
    cli
    push qword 0
    push qword 0
    lidt [rsp]
    int3
.halt:
    hlt
    jmp .halt

; -----------------------------------------------------------------------------
; Freestanding Native AOT Atomic Primitives
; -----------------------------------------------------------------------------
global AtomicIncrement32
AtomicIncrement32:
    mov eax, 1
    lock xadd dword [rcx], eax
    inc eax
    ret

global AtomicDecrement32
AtomicDecrement32:
    mov eax, -1
    lock xadd dword [rcx], eax
    dec eax
    ret

global AtomicCompareExchange32
AtomicCompareExchange32:
    mov eax, r8d
    lock cmpxchg dword [rcx], edx
    ret

global AtomicCompareExchange64
AtomicCompareExchange64:
    mov rax, r8
    lock cmpxchg qword [rcx], rdx
    ret

global AtomicExchange32
AtomicExchange32:
    mov eax, edx
    xchg dword [rcx], eax
    ret

global AtomicFetchAndAdd32
AtomicFetchAndAdd32:
    mov eax, edx
    lock xadd dword [rcx], eax
    ret

global CpuPause
CpuPause:
    pause
    ret

; -----------------------------------------------------------------------------
; 1-Cycle Per-CPU GS-Base Accessors
; GS_BASE points to PerCpuData struct:
; offset 0:  int CoreIndex
; offset 4:  byte ApicId
; offset 8:  ThreadControlBlock* CurrentThread
; offset 16: ulong KernelRsp
; offset 24: ulong UserRspScratch
; -----------------------------------------------------------------------------
global GetCurrentCoreIndex
GetCurrentCoreIndex:
    mov eax, [gs:0]
    ret

global GetCurrentThread
GetCurrentThread:
    mov rax, [gs:8]
    ret

global SetCurrentThread
SetCurrentThread:
    mov [gs:8], rcx
    ret

; -----------------------------------------------------------------------------
; Application Processor (AP) 64-bit Long Mode Entry Thunk
; Jumped to by ApTrampoline long64_entry
; -----------------------------------------------------------------------------
global ApEntry64
extern ApStartupHandler
ApEntry64:
    ; 1. Enable SSE & AVX in CR4 and CR0 before entering any C# code
    mov rax, cr4
    or rax, 0x40600        ; OSFXSR (bit 9), OSXMMEXCPT (bit 10), OSXSAVE (bit 18)
    mov cr4, rax

    mov rax, cr0
    and rax, ~4            ; Clear EM (bit 2)
    or rax, 2              ; Set MP (bit 1)
    mov cr0, rax

    xor ecx, ecx           ; XCR0
    mov eax, 7             ; x87 (bit 0) | SSE (bit 1) | AVX (bit 2)
    xor edx, edx
    xsetbv

    ; 2. Read Local APIC ID from MMIO (0xFFFF8000FEE00020)
    mov rdx, 0xFFFF8000FEE00020
    mov eax, [rdx]
    shr eax, 24
    movzx ecx, al          ; ecx = apicId (Windows x64 ABI)
    movzx edi, al          ; edi = apicId (SysV AMD64 ABI)

    ; Fetch execution stack from ApInitialStacks[apicId]
    lea rdx, [rel ApInitialStacks]
    mov rsp, [rdx + rcx*8]
    test rsp, rsp
    jz .ap_halt
    and rsp, -16

    ; Call C# ApStartupHandler(ulong apicId)
    call ApStartupHandler

.ap_halt:
    cli
    hlt
    jmp .ap_halt

global GetApEntry64
GetApEntry64:
    lea rax, [rel ApEntry64]
    ret

global SetApInitialStack
SetApInitialStack:
    lea r8, [rel ApInitialStacks]
    mov [r8 + rcx*8], rdx
    ret

global GetApTrampolineBinary
GetApTrampolineBinary:
    lea rax, [rel ApTrampolineBinaryStart]
    ret

global GetApTrampolineBinarySize
GetApTrampolineBinarySize:
    mov rax, ApTrampolineBinaryEnd - ApTrampolineBinaryStart
    ret

section .data
global ApInitialStacks
align 16
ApInitialStacks:
    times 16 dq 0

section .rodata
global ApTrampolineBinaryStart
global ApTrampolineBinaryEnd
align 16
ApTrampolineBinaryStart:
    incbin "src/kernel/Arch/x86_64/Assembly/ApTrampoline.bin"
ApTrampolineBinaryEnd:


