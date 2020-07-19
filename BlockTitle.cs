namespace BitFab.KW1281Test
{
    public enum BlockTitle : byte
    {
        ReadIdent = 0x00,
        ClearErrors = 0x05,
        End = 0x06, // end output, end of communication
        GetErrors = 0x07, // get errors, all errors output
        ACK = 0x09,
        NAK = 0x0A,
        ReadEeprom = 0x19,
        WriteEeprom = 0x1A,
        Custom = 0x1B,
        GroupReading = 0x29,
        Login = 0x2B,
        GroupReadingResponse = 0xE7,
        ReadEepromResponse = 0xEF,
        AsciiData = 0xF6,
        WriteEepromResponse = 0xF9,
        GetErrorsResponse = 0xFC,
    }

    // http://nefariousmotorsports.com/forum/index.php?topic=8274.0
#if false
Block title		Answer	
$09	Acknowledge	
$0A	NoAck	
$06	Output end	
$00	ECU identification read	$F6
$07	Fault codes read	$FC
$05	Fault codes delete	$FC
$04	Actuator test	$F5
$11	Basic setting read	$F4
$12	Measuring values read	$F4
$28	Basic setting normed read	$E7
$29	Measuring values normed read	$E7
$10	Parameter coding	$F6
$21	Adaption read	$E6
$22	Adaption transfer	$E6
$2A	Adaption save	$E6
$2B	Login request	$09/$FD
$1B	Custom usage	
$08	ADC channel read	$FB
$01	RAM cells read	$FE
$03	ROM/EPROM/EEPROM read	$FD
$0C	EPROM/EEPROM write	$F9
$19	EEPROM (serial) read	$EF
$1A	EEPROM (serial) write	$F9
$D7	Security access mode 1	$3D
$3D	Security access mode 2	$09/$D7
$02	RAM cells write	$09
$27	Start download routine	$E8
#endif
}
