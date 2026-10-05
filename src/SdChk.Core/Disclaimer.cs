// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core;

public static class Disclaimer
{
    public const string ConfirmPhrase = "I UNDERSTAND";

    public const string Text =
        "WARNING: this test writes test data into the FREE SPACE of the card. If the card is FAKE and " +
        "wraps around (it reports more capacity than it really has), these writes can OVERWRITE AND " +
        "DESTROY EXISTING FILES. BACK UP ALL FILES FIRST. Use at your own risk: the software is provided " +
        "without warranty of any kind (GPL-3.0-or-later), and the authors are not liable for data loss.";
}
