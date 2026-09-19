#!/usr/bin/env python3
"""C166/C167 disassembler.

Library API:
    decode_all(data: bytes, base: int, start: int = 0, end: int|None = None)
        -> list of Instr namedtuples (offset, address, raw, mnemonic, operands, target, cc)

CLI:
    python3 c166dis.py <bin> <base_hex> [start_offset_hex] [end_offset_hex]

Addressing/encoding derived and validated against the Keil C166 assembler
output of the EDC15 Loader.a66 / Loader-sector-erase.a66 sources.
"""
import sys
from collections import namedtuple

Instr = namedtuple("Instr", "offset address raw mnemonic operands target cc")

# ---------------------------------------------------------------- symbols
BYTE_REGS = ['RL0','RH0','RL1','RH1','RL2','RH2','RL3','RH3',
             'RL4','RH4','RL5','RH5','RL6','RH6','RL7','RH7']

CC = {0:'cc_UC',1:'cc_NET',2:'cc_Z',3:'cc_NZ',4:'cc_V',5:'cc_NV',
      6:'cc_N',7:'cc_NN',8:'cc_C',9:'cc_NC',0xA:'cc_SGT',0xB:'cc_SLE',
      0xC:'cc_SLT',0xD:'cc_SGE',0xE:'cc_UGT',0xF:'cc_ULE'}

# byte-bit-offset -> SFR name (bit-addressable region)
BITOFF_NAMES = {0x88:'PSW',0xD8:'S0CON',0xB7:'S0RIC',0xB6:'S0TIC',0xE0:'P2',0xE1:'DP2'}
# specifically named single bits
NAMED_BITS = {(0x88,11):'IEN',(0x88,1):'C',(0xD8,4):'S0REN',(0xB7,7):'S0RIR',
              (0xB6,7):'S0TIR'}
# 16-bit mem operand -> SFR name
MEM_NAMES = {0xFEB0:'S0TBUF',0xFEB2:'S0RBUF',0xFEB4:'S0BG',
             0xFE02:'DPP1',0xFE04:'DPP2',0xFE06:'DPP3',0xFE10:'CP',
             0xFF6C:'S0TIC',0xFF6E:'S0RIC',0xFE00:'DPP0',
             0xFF10:'PSW',0xFFB0:'S0CON',0xFFC0:'P2',0xFFC2:'DP2'}

PROTECTED = {0xA5:'DISWDT',0xA7:'SRVWDT',0xB5:'EINIT',0xB7:'SRST',
             0x87:'IDLE',0x97:'PWRDN'}


def fmt_hex(v, width=0):
    h = ('%0*X' % (width, v)) if width else ('%X' % v)
    if h[0] in 'ABCDEF':
        h = '0' + h
    return h + 'H'


def imm(v):
    return '#' + fmt_hex(v)


def mem_name(addr):
    if addr in MEM_NAMES:
        return MEM_NAMES[addr]
    return fmt_hex(addr, 4)


def bit_name(bitoff, bit):
    if (bitoff, bit) in NAMED_BITS:
        return NAMED_BITS[(bitoff, bit)]
    if bitoff >= 0xF0:
        return 'R%d.%d' % (bitoff - 0xF0, bit)
    if bitoff in BITOFF_NAMES:
        return '%s.%d' % (BITOFF_NAMES[bitoff], bit)
    if bitoff >= 0x80:
        addr = 0xFF00 + 2 * (bitoff - 0x80)
    else:
        addr = 0xFD00 + 2 * bitoff
    return '%s.%d' % (fmt_hex(addr, 4), bit)


def rw(n):
    return 'R%d' % n


def rb(n):
    return BYTE_REGS[n]


def reg8(r, byte=False):
    """8-bit reg field: 0xF0-0xFF = GPR, else short SFR."""
    if r >= 0xF0:
        return rb(r - 0xF0) if byte else rw(r - 0xF0)
    if r < 0x80:
        addr = 0xFE00 + 2 * r
    else:
        addr = 0xFF00 + 2 * (r - 0x80)
    return mem_name(addr)


def s8(v):
    return v - 256 if v >= 128 else v


# short "reg,[Rwi]/[Rwi+]/#data3" second-nibble decoder
def m8(m4):
    if m4 <= 7:
        return imm(m4), None
    if m4 <= 0xB:
        return '[R%d]' % (m4 & 3), None
    return '[R%d+]' % (m4 & 3), None


# opcode -> (mnemonic, format)
SPEC = {
 0x00:('ADD','RR'),0x01:('ADDB','RRB'),0x02:('ADD','REGMEM'),0x03:('ADDB','REGMEMB'),
 0x04:('ADD','MEMREG'),0x05:('ADDB','MEMREGB'),0x06:('ADD','REGD16'),0x07:('ADDB','REGD16B'),
 0x08:('ADD','RNM8'),0x09:('ADDB','RNM8B'),0x0A:('BFLDL','BFLD'),0x0C:('ROL','SHREG'),
 0x10:('ADDC','RR'),0x11:('ADDCB','RRB'),0x12:('ADDC','REGMEM'),0x13:('ADDCB','REGMEMB'),
 0x14:('ADDC','MEMREG'),0x15:('ADDCB','MEMREGB'),0x16:('ADDC','REGD16'),0x17:('ADDCB','REGD16B'),
 0x18:('ADDC','RNM8'),0x19:('ADDCB','RNM8B'),0x1A:('BFLDH','BFLD'),0x1C:('ROL','SHIMM'),
 0x20:('SUB','RR'),0x21:('SUBB','RRB'),0x22:('SUB','REGMEM'),0x23:('SUBB','REGMEMB'),
 0x24:('SUB','MEMREG'),0x25:('SUBB','MEMREGB'),0x26:('SUB','REGD16'),0x27:('SUBB','REGD16B'),
 0x28:('SUB','RNM8'),0x29:('SUBB','RNM8B'),0x2A:('BCMP','BITBIT'),0x2C:('ROR','SHREG'),
 0x30:('SUBC','RR'),0x31:('SUBCB','RRB'),0x32:('SUBC','REGMEM'),0x33:('SUBCB','REGMEMB'),
 0x34:('SUBC','MEMREG'),0x35:('SUBCB','MEMREGB'),0x36:('SUBC','REGD16'),0x37:('SUBCB','REGD16B'),
 0x38:('SUBC','RNM8'),0x39:('SUBCB','RNM8B'),0x3A:('BMOVN','BITBIT'),0x3C:('ROR','SHIMM'),
 0x40:('CMP','RR'),0x41:('CMPB','RRB'),0x42:('CMP','REGMEM'),0x43:('CMPB','REGMEMB'),
 0x46:('CMP','REGD16'),0x47:('CMPB','REGD16B'),0x48:('CMP','RNM8'),0x49:('CMPB','RNM8B'),
 0x4A:('BMOV','BITBIT'),0x4C:('SHL','SHREG'),
 0x50:('XOR','RR'),0x51:('XORB','RRB'),0x52:('XOR','REGMEM'),0x53:('XORB','REGMEMB'),
 0x54:('XOR','MEMREG'),0x55:('XORB','MEMREGB'),0x56:('XOR','REGD16'),0x57:('XORB','REGD16B'),
 0x58:('XOR','RNM8'),0x59:('XORB','RNM8B'),0x5A:('BOR','BITBIT'),0x5C:('SHL','SHIMM'),
 0x60:('AND','RR'),0x61:('ANDB','RRB'),0x62:('AND','REGMEM'),0x63:('ANDB','REGMEMB'),
 0x64:('AND','MEMREG'),0x65:('ANDB','MEMREGB'),0x66:('AND','REGD16'),0x67:('ANDB','REGD16B'),
 0x68:('AND','RNM8'),0x69:('ANDB','RNM8B'),0x6A:('BAND','BITBIT'),0x6C:('SHR','SHREG'),
 0x70:('OR','RR'),0x71:('ORB','RRB'),0x72:('OR','REGMEM'),0x73:('ORB','REGMEMB'),
 0x74:('OR','MEMREG'),0x75:('ORB','MEMREGB'),0x76:('OR','REGD16'),0x77:('ORB','REGD16B'),
 0x78:('OR','RNM8'),0x79:('ORB','RNM8B'),0x7A:('BXOR','BITBIT'),0x7C:('SHR','SHIMM'),
 0x80:('CMPI1','CMPI'),0x82:('CMPI1','CMPMEM'),0x84:('MOV','MEMPTR_ST'),0x86:('CMPI1','CMPD16'),
 0x88:('MOV','PUSH'),0x89:('MOVB','PUSHB'),0x8A:('JB','BITJMP'),
 0x90:('CMPI2','CMPI'),0x92:('CMPI2','CMPMEM'),0x94:('MOV','MEMPTR_LD'),0x96:('CMPI2','CMPD16'),
 0x98:('MOV','RN_PINC'),0x99:('MOVB','RN_PINCB'),0x9A:('JNB','BITJMP'),0x9B:('TRAP','TRAP'),
 0x9C:('JMPI','CALLI'),
 0xA0:('CMPD1','CMPI'),0xA2:('CMPD1','CMPMEM'),0xA4:('MOVB','MEMPTR_ST'),0xA6:('CMPD1','CMPD16'),
 0xA8:('MOV','RN_IND'),0xA9:('MOVB','RN_INDB'),0xAA:('JBC','BITJMP'),0xAB:('CALLI','CALLI'),
 0xAC:('ASHR','SHREG'),
 0xB0:('CMPD2','CMPI'),0xB2:('CMPD2','CMPMEM'),0xB4:('MOVB','MEMPTR_LD'),0xB6:('CMPD2','CMPD16'),
 0xB8:('MOV','ST_IND'),0xB9:('MOVB','ST_INDB'),0xBA:('JNBS','BITJMP'),0xBB:('CALLR','CALLR'),
 0xBC:('ASHR','SHIMM'),
 0xC0:('MOVBZ','MOVBZS'),0xC4:('MOV','DISP_ST'),0xC5:('MOVBZ','REGMEM'),0xC6:('SCXT','SCXT'),
 0xC8:('MOV','IND_IND'),0xC9:('MOVB','IND_INDB'),0xCA:('CALLA','CALLA'),0xCB:('RET','R0'),
 0xCC:('NOP','N0'),
 0xD0:('MOVBS','MOVBZS'),0xD4:('MOV','DISP_LD'),0xD5:('MOVBS','REGMEM'),0xD7:('EXTP','EXT'),
 0xD8:('MOV','PINC_IND'),0xD9:('MOVB','PINC_INDB'),0xDA:('CALLS','CALLS'),0xDB:('RETS','R0'),
 0xE0:('MOV','MOVI4'),0xE1:('MOVB','MOVI4B'),0xE4:('MOVB','DISP_STB'),0xE6:('MOV','REGD16'),
 0xE7:('MOVB','REGD16B'),0xE8:('MOV','IND_PINC'),0xE9:('MOVB','IND_PINCB'),0xEA:('JMPA','JMPA'),
 0xEB:('RETP','RETP'),0xEC:('PUSH','PSHPOP'),
 0xF0:('MOV','RR'),0xF1:('MOVB','RRB'),0xF2:('MOV','REGMEM'),0xF3:('MOVB','REGMEMB'),
 0xF4:('MOVB','DISP_LDB'),0xF6:('MOV','MEMREG'),0xF7:('MOVB','MEMREGB'),0xFA:('JMPS','JMPS'),
 0xFB:('RETI','RETI0'),0xFC:('POP','PSHPOP'),
}


def decode_one(data, pos, base):
    """Return Instr for the instruction at data[pos]."""
    op = data[pos]
    addr = base + pos
    hi, lo = op >> 4, op & 0xF

    def mk(n, length, ops='', target=None, cc=None):
        return Instr(pos, addr, bytes(data[pos:pos+length]), n, ops, target, cc)

    # protected 4-byte instructions
    if op in PROTECTED and pos + 3 < len(data) and data[pos+1] in (0x58,0x48,0x5A,0x4A,0x78,0x68):
        # confirm 3rd/4th equal op (protection pattern)
        if data[pos+2] == op and data[pos+3] == op:
            return mk(PROTECTED[op], 4)

    # uniform low-nibble families
    if lo == 0xD:  # JMPR cc,rel
        rel = s8(data[pos+1]); tgt = addr + 2 + 2*rel
        return mk('JMPR', 2, '%s, %s' % (CC[hi], fmt_hex(tgt & 0xFFFF, 4)), target=tgt & 0xFFFF, cc=hi)
    if lo == 0xE:  # BCLR
        return mk('BCLR', 2, bit_name(data[pos+1], hi))
    if lo == 0xF:  # BSET
        return mk('BSET', 2, bit_name(data[pos+1], hi))

    # segment/page extension prefixes (affect the next #irang2 instructions)
    if op == 0xD1:  # ATOMIC / EXTR  (2 bytes)
        b1 = data[pos+1]; irang = ((b1 >> 4) & 3) + 1
        mn = 'EXTR' if (b1 & 0x80) else 'ATOMIC'
        return mk(mn, 2, '#%d' % irang)
    if op == 0xDC:  # EXTS/EXTP/EXTSR/EXTPR Rwm,#irang2  (2 bytes)
        b1 = data[pos+1]; xx = (b1 >> 6) & 3; irang = ((b1 >> 4) & 3) + 1
        mn = ('EXTS', 'EXTP', 'EXTSR', 'EXTPR')[xx]
        return mk(mn, 2, '%s, #%d' % (rw(b1 & 0xF), irang))
    if op == 0xD7:  # EXTS/EXTP/EXTSR/EXTPR #seg|#pag,#irang2  (4 bytes)
        b1 = data[pos+1]; xx = (b1 >> 6) & 3; irang = ((b1 >> 4) & 3) + 1
        mn = ('EXTS', 'EXTP', 'EXTSR', 'EXTPR')[xx]
        val = data[pos+2] | (data[pos+3] << 8)
        arg = data[pos+2] if mn in ('EXTS', 'EXTSR') else val
        return mk(mn, 4, '#%s, #%d' % (fmt_hex(arg), irang))

    if op not in SPEC:
        return mk('DB', 1, fmt_hex(op, 2))
    mn, fmt = SPEC[op]
    b1 = data[pos+1] if pos+1 < len(data) else 0
    n, m = b1 >> 4, b1 & 0xF

    if fmt == 'RR':
        return mk(mn, 2, '%s, %s' % (rw(n), rw(m)))
    if fmt == 'RRB':
        return mk(mn, 2, '%s, %s' % (rb(n), rb(m)))
    if fmt == 'MOVBZS':  # Rwn, Rbm
        return mk(mn, 2, '%s, %s' % (rw(n), rb(m)))
    if fmt == 'RNM8':
        o, _ = m8(m); return mk(mn, 2, '%s, %s' % (rw(n), o))
    if fmt == 'RNM8B':
        o, _ = m8(m); return mk(mn, 2, '%s, %s' % (rb(n), o))
    if fmt == 'MOVI4':   # MOV Rwn,#data4  byte=(data<<4)|n
        return mk(mn, 2, '%s, %s' % (rw(m), imm(n)))
    if fmt == 'MOVI4B':
        return mk(mn, 2, '%s, %s' % (rb(m), imm(n)))
    if fmt == 'CMPI':    # Rwn,#data4  byte=(data<<4)|n
        return mk(mn, 2, '%s, %s' % (rw(m), imm(n)))
    if fmt == 'SHIMM':   # Rwn,#data4  byte=(data<<4)|n
        return mk(mn, 2, '%s, %s' % (rw(m), imm(n)))
    if fmt == 'SHREG':   # Rwn,Rwm
        return mk(mn, 2, '%s, %s' % (rw(n), rw(m)))
    # pointer MOV forms (2 bytes)
    if fmt == 'PUSH':    return mk(mn, 2, '[-%s], %s' % (rw(m), rw(n)))
    if fmt == 'PUSHB':   return mk(mn, 2, '[-%s], %s' % (rw(m), rb(n)))
    if fmt == 'RN_PINC': return mk(mn, 2, '%s, [%s+]' % (rw(n), rw(m)))
    if fmt == 'RN_PINCB':return mk(mn, 2, '%s, [%s+]' % (rb(n), rw(m)))
    if fmt == 'RN_IND':  return mk(mn, 2, '%s, [%s]' % (rw(n), rw(m)))
    if fmt == 'RN_INDB': return mk(mn, 2, '%s, [%s]' % (rb(n), rw(m)))
    if fmt == 'ST_IND':  return mk(mn, 2, '[%s], %s' % (rw(m), rw(n)))
    if fmt == 'ST_INDB': return mk(mn, 2, '[%s], %s' % (rw(m), rb(n)))
    if fmt == 'IND_IND': return mk(mn, 2, '[%s], [%s]' % (rw(n), rw(m)))
    if fmt == 'IND_INDB':return mk(mn, 2, '[%s], [%s]' % (rw(n), rw(m)))
    if fmt == 'PINC_IND':return mk(mn, 2, '[%s+], [%s]' % (rw(n), rw(m)))
    if fmt == 'PINC_INDB':return mk(mn, 2, '[%s+], [%s]' % (rw(n), rw(m)))
    if fmt == 'IND_PINC':return mk(mn, 2, '[%s], [%s+]' % (rw(n), rw(m)))
    if fmt == 'IND_PINCB':return mk(mn, 2, '[%s], [%s+]' % (rw(n), rw(m)))
    # 4-byte forms
    d16 = (data[pos+2] | (data[pos+3] << 8)) if pos+3 < len(data) else 0
    if fmt == 'REGMEM':  return mk(mn, 4, '%s, %s' % (reg8(b1), mem_name(d16)))
    if fmt == 'REGMEMB': return mk(mn, 4, '%s, %s' % (reg8(b1, True), mem_name(d16)))
    if fmt == 'MEMREG':  return mk(mn, 4, '%s, %s' % (mem_name(d16), reg8(b1)))
    if fmt == 'MEMREGB': return mk(mn, 4, '%s, %s' % (mem_name(d16), reg8(b1, True)))
    if fmt == 'REGD16':  return mk(mn, 4, '%s, %s' % (reg8(b1), imm(d16)))
    if fmt == 'REGD16B': return mk(mn, 4, '%s, %s' % (reg8(b1, True), imm(d16)))
    if fmt == 'DISP_ST': return mk(mn, 4, '[%s+%s], %s' % (rw(m), imm(d16), rw(n)))
    if fmt == 'DISP_LD': return mk(mn, 4, '%s, [%s+%s]' % (rw(n), rw(m), imm(d16)))
    if fmt == 'DISP_STB':return mk(mn, 4, '[%s+%s], %s' % (rw(m), imm(d16), rb(n)))
    if fmt == 'DISP_LDB':return mk(mn, 4, '%s, [%s+%s]' % (rb(n), rw(m), imm(d16)))
    if fmt == 'MEMPTR_ST':  # [Rwn], mem
        return mk(mn, 4, '[%s], %s' % (rw(m), mem_name(d16)))
    if fmt == 'MEMPTR_LD':  # mem, [Rwn]
        return mk(mn, 4, '%s, [%s]' % (mem_name(d16), rw(m)))
    if fmt == 'CMPMEM':  return mk(mn, 4, '%s, %s' % (rw(m), mem_name(d16)))
    if fmt == 'CMPD16':  return mk(mn, 4, '%s, %s' % (rw(m), imm(d16)))
    if fmt == 'SCXT':    return mk(mn, 4, '%s, %s' % (reg8(b1), imm(d16)))
    if fmt == 'BITBIT':
        x, y, z = data[pos+1], data[pos+2], data[pos+3]
        op1 = bit_name(y, z & 0xF); op2 = bit_name(x, z >> 4)
        return mk(mn, 4, '%s, %s' % (op1, op2))
    if fmt == 'BITJMP':
        bitoff, rel, bp = data[pos+1], s8(data[pos+2]), data[pos+3] >> 4
        tgt = (addr + 4 + 2*rel) & 0xFFFF
        return mk(mn, 4, '%s, %s' % (bit_name(bitoff, bp), fmt_hex(tgt, 4)), target=tgt)
    if fmt == 'BFLD':
        return mk(mn, 4, '%s, %s, %s' % (bit_name(data[pos+1], 0), imm(data[pos+2]), imm(data[pos+3])))
    if fmt == 'CALLA':
        cc = b1 >> 4 if (b1 & 0xF) == 0 else b1 & 0xF
        return mk(mn, 4, '%s, %s' % (CC[cc], fmt_hex(d16, 4)), target=d16, cc=cc)
    if fmt == 'JMPA':
        cc = b1 >> 4 if (b1 & 0xF) == 0 else b1 & 0xF
        return mk(mn, 4, '%s, %s' % (CC[cc], fmt_hex(d16, 4)), target=d16, cc=cc)
    if fmt == 'CALLS':
        return mk(mn, 4, '%s, %s' % (fmt_hex(b1, 2), fmt_hex(d16, 4)), target=d16)
    if fmt == 'JMPS':
        return mk(mn, 4, '%s, %s' % (fmt_hex(b1, 2), fmt_hex(d16, 4)), target=d16)
    if fmt == 'CALLR':
        rel = s8(data[pos+1]); tgt = (addr + 2 + 2*rel) & 0xFFFF
        return mk(mn, 2, fmt_hex(tgt, 4), target=tgt)
    if fmt == 'CALLI':
        cc = b1 >> 4
        return mk(mn, 2, '%s, [%s]' % (CC[cc], rw(m)))
    if fmt == 'RETP':  return mk(mn, 2, reg8(b1))
    if fmt == 'PSHPOP':return mk(mn, 2, reg8(b1))
    if fmt == 'TRAP':  return mk(mn, 2, imm(b1 >> 1))
    if fmt == 'R0':    return mk(mn, 2)
    if fmt == 'N0':    return mk(mn, 2)
    if fmt == 'RETI0': return mk(mn, 2)
    if fmt == 'EXT':   return mk(mn, 2, '(prefix)')
    return mk('DB', 1, fmt_hex(op, 2))


def decode_all(data, base, start=0, end=None):
    if end is None:
        end = len(data)
    out = []
    pos = start
    while pos < end:
        ins = decode_one(data, pos, base)
        out.append(ins)
        pos += len(ins.raw)
    return out


def format_listing(insns):
    lines = []
    for i in insns:
        raw = ' '.join('%02X' % b for b in i.raw)
        ops = i.operands
        lines.append(('%04X:  %-14s %-7s %s' % (i.address, raw, i.mnemonic, ops)).rstrip())
    return '\n'.join(lines)


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(1)
    data = open(sys.argv[1], 'rb').read()
    base = int(sys.argv[2], 16)
    start = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0
    end = int(sys.argv[4], 16) if len(sys.argv) > 4 else None
    print(format_listing(decode_all(data, base, start, end)))
