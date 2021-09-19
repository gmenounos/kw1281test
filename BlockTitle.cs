namespace BitFab.KW1281Test
{
    public enum BlockTitle : byte
    {
        ReadIdent = 0x00,
        ReadRomEeprom = 0x03,
        ActuatorTest = 0x04,
        FaultCodesDelete = 0x05,
        End = 0x06, // end output, end of communication
        FaultCodesRead = 0x07, // get errors, all errors output
        ACK = 0x09,
        NAK = 0x0A,
        SoftwareCoding = 0x10,
        ReadEeprom = 0x19,
        WriteEeprom = 0x1A,
        Custom = 0x1B,
        AdaptationRead = 0x21,
        AdaptationTest = 0x22,
        GroupReading = 0x29,
        AdaptationSave = 0x2A,
        Login = 0x2B,
        AdaptationResponse = 0xE6,
        GroupReadingResponse = 0xE7,
        ReadEepromResponse = 0xEF,
        ActuatorTestResponse = 0xF5,
        AsciiData = 0xF6,
        WriteEepromResponse = 0xF9,
        FaultCodesResponse = 0xFC,
        ReadRomEepromResponse = 0xFD,
    }

    // http://nefariousmotorsports.com/forum/index.php?topic=8274.0
#if false
Block title		Answer	
$00	ECU identification read	$F6
$01	RAM cells read	$FE
$02	RAM cells write	$09
$03	ROM/EPROM/EEPROM read	$FD
$04	Actuator test	$F5
$05	Fault codes delete	$FC
$06	Output end	
$07	Fault codes read	$FC
$08	ADC channel read	$FB
$09	Acknowledge	
$0A	NoAck	
$0C	EPROM/EEPROM write	$F9
$10	Parameter coding	$F6
$11	Basic setting read	$F4
$12	Measuring values read	$F4
$19	EEPROM (serial) read	$EF
$1A	EEPROM (serial) write	$F9
$1B	Custom usage	
$21	Adaption read	$E6
$22	Adaption transfer	$E6
$27	Start download routine	$E8
$28	Basic setting normed read	$E7
$29	Measuring values normed read	$E7
$2A	Adaption save	$E6
$2B	Login request	$09/$FD
$3D	Security access mode 2	$09/$D7
$D7	Security access mode 1	$3D
#endif
}
