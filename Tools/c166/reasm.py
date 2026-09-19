#!/usr/bin/env python3
"""Minimal C166 re-assembler used to prove that a generated .a66 source
re-encodes to the exact original bytes (a stand-in for a Keil round-trip).

It independently computes every opcode/displacement from the mnemonic +
operands + resolved labels (it does NOT copy bytes from the source), so a
byte-for-byte match against the original .bin is strong evidence the source
assembles identically.  Encoding rules were validated: c166dis decodes
Loader.a66 / Loader-sector-erase.a66 at 100%, and this module re-encodes the
same instructions back.

Usage:
    python3 reasm.py <source.a66> <expected.bin>
API:
    assemble(path) -> (bytes, base)
"""
import sys, re
import c166dis as D

# reverse tables ----------------------------------------------------------
BREG_IDX = {n: i for i, n in enumerate(D.BYTE_REGS)}
CC_REV = {v.lower(): k for k, v in D.CC.items()}
MEM_REV = {v: k for k, v in D.MEM_NAMES.items()}
NAMED_BIT_REV = {v.upper(): k for k, v in D.NAMED_BITS.items()}
BITOFF_REV = {v: k for k, v in D.BITOFF_NAMES.items()}

ALU_BASE = {'ADD':0x00,'ADDB':0x01,'ADDC':0x10,'ADDCB':0x11,'SUB':0x20,'SUBB':0x21,
            'SUBC':0x30,'SUBCB':0x31,'CMP':0x40,'CMPB':0x41,'AND':0x60,'ANDB':0x61,
            'OR':0x70,'ORB':0x71,'XOR':0x50,'XORB':0x51}
SH_IMM = {'SHL':0x5C,'SHR':0x7C,'ROL':0x1C,'ROR':0x3C,'ASHR':0xBC}
SH_REG = {'SHL':0x4C,'SHR':0x6C,'ROL':0x0C,'ROR':0x2C,'ASHR':0xAC}
CMPX = {'CMPI1':0x80,'CMPI2':0x90,'CMPD1':0xA0,'CMPD2':0xB0}
BITBIT = {'BMOV':0x4A,'BAND':0x6A,'BOR':0x5A,'BXOR':0x7A,'BCMP':0x2A,'BMOVN':0x3A}
BITJMP = {'JB':0x8A,'JNB':0x9A,'JBC':0xAA,'JNBS':0xBA}
PROT = {'SRVWDT':(0xA7,0x58),'SRST':(0xB7,0x48),'DISWDT':(0xA5,0x5A),
        'EINIT':(0xB5,0x4A),'IDLE':(0x87,0x78),'PWRDN':(0x97,0x68)}


def num(tok):
    """Parse a numeric literal (hex '..H', '0x..', or decimal)."""
    t = tok.strip()
    if t.startswith('#'):
        t = t[1:]
    t = t.strip()
    if t[:2].lower() == '0x':
        return int(t[2:], 16)
    if t and t[-1] in 'Hh':
        return int(t[:-1], 16)
    return int(t, 10)


def is_wreg(t):
    return bool(re.fullmatch(r'R\d+', t.strip(), re.I))

def is_breg(t):
    return t.strip().upper() in BREG_IDX

def wn(t):
    return int(t.strip()[1:])

def bidx(t):
    return BREG_IDX[t.strip().upper()]

def regfield(t):
    """8-bit reg field for a GPR operand (word or byte)."""
    t = t.strip()
    if is_wreg(t):
        return 0xF0 + wn(t)
    if is_breg(t):
        return 0xF0 + bidx(t)
    if t.upper() in MEM_REV:
        return sfr_short(MEM_REV[t.upper()])
    return sfr_short(num(t))

def sfr_short(addr):
    if addr >= 0xFF00:
        return 0x80 + (addr - 0xFF00) // 2
    return (addr - 0xFE00) // 2

def mem_addr(t):
    t = t.strip()
    if t.upper() in MEM_REV:
        return MEM_REV[t.upper()]
    return num(t)

def parse_mem(t):
    """[Rn] / [Rn+] / [-Rn] / [Rn+#disp] -> tuple."""
    inner = t.strip()[1:-1].strip()
    if inner.startswith('-'):
        return ('PDEC', int(inner[2:]))
    if inner.endswith('+'):
        return ('PINC', int(inner[1:-1]))
    m = re.match(r'R(\d+)\+(#?[0-9A-Fa-fHhx]+)$', inner)
    if m:
        return ('DISP', int(m.group(1)), num(m.group(2)))
    return ('IND', int(inner[1:]))

def parse_bit(t):
    t = t.strip()
    u = t.upper()
    if u in NAMED_BIT_REV:
        return NAMED_BIT_REV[u]
    name, _, b = t.partition('.')
    bit = int(b)
    name = name.strip()
    if re.fullmatch(r'R\d+', name, re.I):
        return (0xF0 + int(name[1:]), bit)
    if name in BITOFF_REV:
        return (BITOFF_REV[name], bit)
    addr = num(name)
    if addr >= 0xFF00:
        return (0x80 + (addr - 0xFF00) // 2, bit)
    return ((addr - 0xFD00) // 2, bit)

def split_ops(s):
    return [x.strip() for x in s.split(',')] if s.strip() else []

def s8(v):
    return v & 0xFF


# ---- instruction encoding ----------------------------------------------
def enc_instr(mn, ops, addr, resolve):
    """Return list[int] bytes. resolve(sym)->addr for labels/targets."""
    mn = mn.upper()
    o = split_ops(ops)

    if mn in PROT:
        a, b = PROT[mn]; return [a, b, a, a]
    if mn == 'ATOMIC':
        return [0xD1, ((num(o[0])-1)&3) << 4]
    if mn == 'EXTR':
        return [0xD1, 0x80 | (((num(o[0])-1)&3) << 4)]
    if mn in ('EXTS','EXTP','EXTSR','EXTPR'):
        xx = {'EXTS':0,'EXTP':1,'EXTSR':2,'EXTPR':3}[mn]
        irang = (num(o[1]) - 1) & 3
        if o[0].startswith('#'):        # #seg/#pag form -> D7 (4 bytes)
            v = num(o[0])
            return [0xD7, (xx<<6)|(irang<<4), v & 0xFF, (v>>8) & 0xFF]
        return [0xDC, (xx<<6)|(irang<<4)|wn(o[0])]   # Rwm form -> DC (2 bytes)
    if mn == 'RET':  return [0xCB, 0x00]
    if mn == 'RETS': return [0xDB, 0x00]
    if mn == 'NOP':  return [0xCC, 0x00]
    if mn == 'RETI': return [0xFB, 0x88]
    if mn in ('RETP','PUSH','POP'):
        op = {'RETP':0xEB,'PUSH':0xEC,'POP':0xFC}[mn]; return [op, regfield(o[0])]

    if mn in ALU_BASE:
        base = ALU_BASE[mn]; a, b = o[0], o[1]
        if (is_wreg(a) and is_wreg(b)):
            return [base, (wn(a) << 4) | wn(b)]
        if (is_breg(a) and is_breg(b)):
            return [base, (bidx(a) << 4) | bidx(b)]
        n = wn(a) if is_wreg(a) else bidx(a)
        if b.startswith('#'):
            v = num(b)
            if (is_wreg(a) or is_breg(a)) and 0 <= v <= 7:
                return [base + 8, (n << 4) | v]
            lo, hi = v & 0xFF, (v >> 8) & 0xFF
            return [base + 6, regfield(a), lo, hi]
        if b.startswith('['):
            k, *rest = parse_mem(b)
            if k == 'IND':  return [base + 8, (n << 4) | (0x8 + rest[0])]
            if k == 'PINC': return [base + 8, (n << 4) | (0xC + rest[0])]
        if b.startswith('['):  # unreachable safety
            pass
        # reg,mem  or mem,reg
        if b[0] not in '[#' and not is_wreg(b) and not is_breg(b):
            return [base + 2, regfield(a), mem_addr(b) & 0xFF, mem_addr(b) >> 8]
        raise ValueError('ALU form %s %s' % (mn, ops))

    if mn == 'MOV':
        a, b = o[0], o[1]
        if is_wreg(a) and is_wreg(b): return [0xF0, (wn(a)<<4)|wn(b)]
        if b.startswith('#'):
            v = num(b)
            if is_wreg(a) and 0 <= v <= 15: return [0xE0, (v<<4)|wn(a)]
            return [0xE6, regfield(a), v&0xFF, v>>8]
        if a.startswith('[') and b[0] not in '[#' and not is_wreg(b):  # MOV [Rn],mem
            ad=mem_addr(b); return [0x84, parse_mem(a)[1], ad&0xFF, ad>>8]
        if b.startswith('[') and a[0] not in '[#' and not is_wreg(a):  # MOV mem,[Rn]
            ad=mem_addr(a); return [0x94, parse_mem(b)[1], ad&0xFF, ad>>8]
        if is_wreg(a) and b.startswith('['):
            k,*r=parse_mem(b)
            if k=='PINC': return [0x98,(wn(a)<<4)|r[0]]
            if k=='IND':  return [0xA8,(wn(a)<<4)|r[0]]
            if k=='DISP': return [0xD4,(wn(a)<<4)|r[0], r[1]&0xFF, r[1]>>8]
        if a.startswith('[') and is_wreg(b):
            k,*r=parse_mem(a)
            if k=='PDEC': return [0x88,(wn(b)<<4)|r[0]]
            if k=='IND':  return [0xB8,(wn(b)<<4)|r[0]]
            if k=='DISP': return [0xC4,(wn(b)<<4)|r[0], r[1]&0xFF, r[1]>>8]
        if is_wreg(a) and b[0] not in '[#':  # MOV reg,mem
            ad=mem_addr(b); return [0xF2, 0xF0+wn(a), ad&0xFF, ad>>8]
        if a[0] not in '[#' and is_wreg(b):  # MOV mem,reg
            ad=mem_addr(a); return [0xF6, 0xF0+wn(b), ad&0xFF, ad>>8]
        raise ValueError('MOV form %s' % ops)

    if mn == 'MOVB':
        a, b = o[0], o[1]
        if is_breg(a) and is_breg(b): return [0xF1,(bidx(a)<<4)|bidx(b)]
        if b.startswith('#'):
            v=num(b)
            if is_breg(a) and 0<=v<=15: return [0xE1,(v<<4)|bidx(a)]
            return [0xE7, regfield(a), v&0xFF, v>>8]
        if a.startswith('[') and b[0] not in '[#' and not is_breg(b):  # MOVB [Rn],mem
            ad=mem_addr(b); return [0xA4, parse_mem(a)[1], ad&0xFF, ad>>8]
        if b.startswith('[') and a[0] not in '[#' and not is_breg(a):  # MOVB mem,[Rn]
            ad=mem_addr(a); return [0xB4, parse_mem(b)[1], ad&0xFF, ad>>8]
        if is_breg(a) and b.startswith('['):
            k,*r=parse_mem(b)
            if k=='PINC': return [0x99,(bidx(a)<<4)|r[0]]
            if k=='IND':  return [0xA9,(bidx(a)<<4)|r[0]]
            if k=='DISP': return [0xF4,(bidx(a)<<4)|r[0], r[1]&0xFF, r[1]>>8]
        if a.startswith('[') and is_breg(b):
            k,*r=parse_mem(a)
            if k=='PDEC': return [0x89,(bidx(b)<<4)|r[0]]
            if k=='IND':  return [0xB9,(bidx(b)<<4)|r[0]]
            if k=='DISP': return [0xE4,(bidx(b)<<4)|r[0], r[1]&0xFF, r[1]>>8]
        if is_breg(a) and b[0] not in '[#':
            ad=mem_addr(b); return [0xF3, 0xF0+bidx(a), ad&0xFF, ad>>8]
        if a[0] not in '[#' and is_breg(b):
            ad=mem_addr(a); return [0xF7, 0xF0+bidx(b), ad&0xFF, ad>>8]
        raise ValueError('MOVB form %s' % ops)

    if mn in ('MOVBZ','MOVBS'):
        op = 0xC0 if mn=='MOVBZ' else 0xD0
        return [op, (wn(o[0])<<4)|bidx(o[1])]

    if mn in SH_IMM:
        a,b=o[0],o[1]
        if b.startswith('#'):
            return [SH_IMM[mn], (num(b)<<4)|wn(a)]
        return [SH_REG[mn], (wn(a)<<4)|wn(b)]

    if mn in CMPX:
        v = num(o[1])
        if 0 <= v <= 15:
            return [CMPX[mn], (v<<4)|wn(o[0])]
        return [CMPX[mn] + 6, 0xF0 | wn(o[0]), v & 0xFF, v >> 8]  # #data16 form

    if mn == 'BCLR':
        bo,bit=parse_bit(o[0]); return [(bit<<4)|0x0E, bo]
    if mn == 'BSET':
        bo,bit=parse_bit(o[0]); return [(bit<<4)|0x0F, bo]
    if mn in BITBIT:
        b1o,b1=parse_bit(o[0]); b2o,b2=parse_bit(o[1])
        return [BITBIT[mn], b2o, b1o, (b2<<4)|b1]
    if mn in BITJMP:
        bo,bit=parse_bit(o[0]); tgt=resolve(o[1])
        rel=((tgt-(addr+4))//2)&0xFF
        return [BITJMP[mn], bo, rel, bit<<4]

    if mn == 'JMPR':
        cc=CC_REV[o[0].lower()]; tgt=resolve(o[1])
        rel=((tgt-(addr+2))//2)&0xFF
        return [(cc<<4)|0x0D, rel]
    if mn == 'CALLR':
        tgt=resolve(o[0]); rel=((tgt-(addr+2))//2)&0xFF
        return [0xBB, rel]
    if mn == 'CALLA':
        cc=CC_REV[o[0].lower()]; tgt=resolve(o[1])
        return [0xCA, (cc<<4)&0xF0, tgt&0xFF, tgt>>8]
    if mn == 'JMPA':
        cc=CC_REV[o[0].lower()]; tgt=resolve(o[1])
        return [0xEA, (cc<<4)&0xF0, tgt&0xFF, tgt>>8]
    if mn == 'CALLS':
        seg=num(o[0]); tgt=resolve(o[1])
        return [0xDA, seg&0xFF, tgt&0xFF, tgt>>8]
    if mn == 'JMPS':
        seg=num(o[0]); tgt=resolve(o[1])
        return [0xFA, seg&0xFF, tgt&0xFF, tgt>>8]

    raise ValueError('unknown mnemonic %s (%s)' % (mn, ops))


def instr_len(mn, ops):
    mn=mn.upper()
    if mn in PROT: return 4
    if mn in ('RET','RETS','NOP','RETI','RETP','PUSH','POP'): return 2
    if mn in ('CALLA','JMPA','CALLS','JMPS') or mn in BITBIT or mn in BITJMP: return 4
    if mn in ('JMPR','CALLR','BCLR','BSET','MOVBZ','MOVBS'): return 2
    if mn in ('ATOMIC','EXTR'): return 2
    if mn in SH_IMM: return 2
    o=split_ops(ops)
    if mn in ('EXTS','EXTP','EXTSR','EXTPR'):
        return 4 if o[0].startswith('#') else 2
    if mn in CMPX:
        return 2 if 0 <= num(o[1]) <= 15 else 4
    if mn in ALU_BASE:
        a,b=o[0],o[1]
        if is_wreg(a) and (is_wreg(b) or is_breg(b)): return 2
        if is_breg(a) and (is_wreg(b) or is_breg(b)): return 2
        if b.startswith('#'):
            v=num(b)
            return 2 if ((is_wreg(a) or is_breg(a)) and 0<=v<=7) else 4
        if b.startswith('['):
            k=parse_mem(b)[0]
            return 2 if k in ('IND','PINC') else 4
        return 4
    if mn in ('MOV','MOVB'):
        a,b=o[0],o[1]
        regcls = is_wreg if mn=='MOV' else is_breg
        if regcls(a) and (is_wreg(b) or is_breg(b)): return 2
        if b.startswith('#'):
            v=num(b); return 2 if (regcls(a) and 0<=v<=15) else 4
        for x in (a,b):
            if x.startswith('[') and parse_mem(x)[0]=='DISP': return 4
        aptr, bptr = a.startswith('['), b.startswith('[')
        if aptr and bptr: return 2                 # [Rn],[Rm]
        if aptr: return 2 if regcls(b) else 4       # [Rn],reg vs [Rn],mem
        if bptr: return 2 if regcls(a) else 4       # reg,[Rn] vs mem,[Rn]
        return 4  # reg,mem / mem,reg
    raise ValueError('len? %s %s'%(mn,ops))


# ---- source parsing -----------------------------------------------------
def assemble(path):
    base = 0
    items = []          # (kind, ...)
    labels = {}
    # pass 0: read lines, split labels
    pending = []
    raw_items = []
    for raw in open(path, encoding='latin-1'):
        line = raw.split(';',1)[0].rstrip('\n')
        if not line.strip() or line.lstrip().startswith('$'):
            continue
        parts = line.split()
        while parts and parts[0].endswith(':'):
            pending.append(parts[0][:-1]); parts=parts[1:]
        if not parts:
            continue
        up1 = parts[1].upper() if len(parts)>1 else ''
        if up1 == 'SECTION':
            m=re.search(r'AT\s+([0-9A-Fa-f]+H)', line)
            if m: base=num(m.group(1))
            continue
        if up1 == 'PROC':
            pending.append(parts[0]); continue
        if up1 in ('ENDP','ENDS','EQU','SET','BIT'):
            continue
        w0=parts[0].upper()
        if w0 == 'END':
            continue
        if w0 == 'DB':
            body=line[line.upper().index('DB')+2:]
            vals=[num(x) for x in body.split(',')]
            raw_items.append(('DB', pending, vals)); pending=[]
            continue
        if w0 == 'DW':
            body=line[line.upper().index('DW')+2:]
            toks=[x.strip() for x in body.split(',')]
            raw_items.append(('DW', pending, toks)); pending=[]
            continue
        mn=parts[0]; ops=' '.join(parts[1:])
        raw_items.append(('I', pending, mn, ops)); pending=[]
    if pending:
        raw_items.append(('END', pending))

    # pass 1: addresses
    addr=base
    for it in raw_items:
        for lab in it[1]:
            labels[lab.upper()]=addr
        if it[0]=='DB': addr+=len(it[2])
        elif it[0]=='DW': addr+=2*len(it[2])
        elif it[0]=='I': addr+=instr_len(it[2], it[3])
    def resolve(sym):
        s=sym.strip()
        if s.upper() in labels: return labels[s.upper()]
        return num(s)
    # pass 2: encode
    out=bytearray(); addr=base
    for it in raw_items:
        if it[0]=='DB':
            out+=bytes(v&0xFF for v in it[2]); addr+=len(it[2])
        elif it[0]=='DW':
            for t in it[2]:
                v=resolve(t); out+=bytes([v&0xFF, (v>>8)&0xFF])
            addr+=2*len(it[2])
        elif it[0]=='I':
            b=enc_instr(it[2], it[3], addr, resolve)
            out+=bytes(b); addr+=len(b)
    return bytes(out), base


# ---------------------------------------------------------------------------
# Bare-CALL resolution: CALLR (near) vs CALLA cc_UC (far) chosen shortest-first by
# fixpoint (matching Keil A166), plus hard range checks on every relative branch.
# ---------------------------------------------------------------------------
CC_UC = CC_REV['cc_uc']  # unconditional condition code (0)


class RangeError(Exception):
    pass


def _sdisp_words(tgt, next_addr):
    """Signed word displacement used by relative branches."""
    d = tgt - next_addr
    assert d % 2 == 0, "odd displacement"
    return d // 2


def call_len(kind):
    return 2 if kind == 'R' else 4


def enc_call(kind, tgt, addr):
    if kind == 'R':
        disp = _sdisp_words(tgt, addr + 2)
        if not (-128 <= disp <= 127):
            raise RangeError("CALLR out of range: disp=%d words @0x%04X" % (disp, addr))
        return [0xBB, disp & 0xFF]
    return [0xCA, (CC_UC << 4) & 0xF0, tgt & 0xFF, (tgt >> 8) & 0xFF]


def enc_checked(mn, ops, addr, resolve):
    """Encode a non-CALL instruction, but hard-check relative-branch ranges."""
    mnU = mn.upper()
    b = enc_instr(mn, ops, addr, resolve)
    o = split_ops(ops)
    if mnU == 'JMPR':
        tgt = resolve(o[1]); disp = _sdisp_words(tgt, addr + 2)
        if not (-128 <= disp <= 127):
            raise RangeError("JMPR out of range: disp=%d words @0x%04X -> %s" % (disp, addr, o[1]))
    elif mnU in BITJMP:
        tgt = resolve(o[1]); disp = _sdisp_words(tgt, addr + 4)
        if not (-128 <= disp <= 127):
            raise RangeError("%s out of range: disp=%d words @0x%04X -> %s" % (mnU, disp, addr, o[1]))
    return b


def assemble2(path):
    # --- reuse reasm's line parser to get raw_items + base ---
    import re
    base = 0
    pending = []
    raw_items = []
    for raw in open(path, encoding='latin-1'):
        line = raw.split(';', 1)[0].rstrip('\n')
        if not line.strip() or line.lstrip().startswith('$'):
            continue
        parts = line.split()
        while parts and parts[0].endswith(':'):
            pending.append(parts[0][:-1]); parts = parts[1:]
        if not parts:
            continue
        up1 = parts[1].upper() if len(parts) > 1 else ''
        if up1 == 'SECTION':
            m = re.search(r'AT\s+([0-9A-Fa-f]+H)', line)
            if m:
                base = num(m.group(1))
            continue
        if up1 == 'PROC':
            pending.append(parts[0]); continue
        if up1 in ('ENDP', 'ENDS', 'EQU', 'SET', 'BIT'):
            continue
        w0 = parts[0].upper()
        if w0 == 'END':
            continue
        if w0 == 'DB':
            body = line[line.upper().index('DB') + 2:]
            vals = [num(x) for x in body.split(',')]
            raw_items.append(('DB', pending, vals)); pending = []
            continue
        if w0 == 'DW':
            body = line[line.upper().index('DW') + 2:]
            toks = [x.strip() for x in body.split(',')]
            raw_items.append(('DW', pending, toks)); pending = []
            continue
        mn = parts[0]; ops = ' '.join(parts[1:])
        raw_items.append(('I', pending, mn, ops)); pending = []
    if pending:
        raw_items.append(('END', pending))

    # --- CALL kinds: iterative shortest-first fixpoint ---
    call_kind = {}
    for i, it in enumerate(raw_items):
        if it[0] == 'I' and it[2].upper() == 'CALL':
            call_kind[i] = 'R'

    def item_len(i, it):
        if it[0] == 'DB':
            return len(it[2])
        if it[0] == 'DW':
            return 2 * len(it[2])
        if it[0] == 'I':
            if it[2].upper() == 'CALL':
                return call_len(call_kind[i])
            return instr_len(it[2], it[3])
        return 0

    labels = {}

    def resolve(sym):
        s = sym.strip()
        return labels[s.upper()] if s.upper() in labels else num(s)

    for _ in range(50):
        addr = base
        labels = {}
        addrs = []
        for i, it in enumerate(raw_items):
            for lab in it[1]:
                labels[lab.upper()] = addr
            addrs.append(addr)
            addr += item_len(i, it)
        changed = False
        for i, it in enumerate(raw_items):
            if it[0] == 'I' and it[2].upper() == 'CALL' and call_kind[i] == 'R':
                tgt = resolve(it[3])
                disp = _sdisp_words(tgt, addrs[i] + 2)
                if not (-128 <= disp <= 127):
                    call_kind[i] = 'A'; changed = True
        if not changed:
            break
    else:
        raise RuntimeError("CALL length resolution did not converge")

    # --- final encode ---
    out = bytearray(); addr = base
    for i, it in enumerate(raw_items):
        if it[0] == 'DB':
            out += bytes(v & 0xFF for v in it[2]); addr += len(it[2])
        elif it[0] == 'DW':
            for t in it[2]:
                v = resolve(t); out += bytes([v & 0xFF, (v >> 8) & 0xFF])
            addr += 2 * len(it[2])
        elif it[0] == 'I':
            if it[2].upper() == 'CALL':
                b = enc_call(call_kind[i], resolve(it[3]), addr)
            else:
                b = enc_checked(it[2], it[3], addr, resolve)
            out += bytes(b); addr += len(b)
    return bytes(out), base, labels


if __name__ == '__main__':
    src = sys.argv[1]
    got, base, labels = assemble2(src)
    print("assembled %s: %d bytes, base 0x%04X" % (src, len(got), base))
    if len(sys.argv) > 2:
        want = open(sys.argv[2], 'rb').read()
        if got == want:
            print("OK  byte-for-byte identical to %s" % sys.argv[2])
        else:
            print("MISMATCH len got=%d want=%d" % (len(got), len(want)))
            for i, (g, w) in enumerate(zip(got, want)):
                if g != w:
                    print("  first diff at 0x%04X (off 0x%X): got %02X want %02X"
                          % (base + i, i, g, w)); break
            sys.exit(1)

