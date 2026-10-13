// Test der Nummernerkennung ohne Outlook:  node outlook-addin/test/extract.test.js
"use strict";
const assert = require("assert");
const Extract = require("../taskpane.js");

let failed = 0;
function test(name, fn) {
    try { fn(); console.log(`  [OK] ${name}`); }
    catch (e) { failed++; console.log(`  [FEHLER] ${name}\n         ${e.message}`); }
}
const nums = text => Extract.numbers(text).map(n => n.number);

test("Signatur mit Tel/Mobil/Fax, Art erkannt, Fax zuletzt", () => {
    const r = Extract.numbers([
        "Max Mustermann",
        "Tel.: +49 (0) 30 123456-78",
        "Fax: +49 (0) 30 123456-99",
        "Mobil: 0171 / 123 45 67",
    ].join("\n"));
    assert.deepStrictEqual(r.map(n => [n.number, n.kind]), [
        ["+49 (0) 30 123456-78", "Telefon"],
        ["0171 / 123 45 67", "Mobil"],
        ["+49 (0) 30 123456-99", "Fax"],
    ]);
});

test("Kurzformen T/M und Klammer-Vorwahl", () => {
    const r = Extract.numbers("T (030) 98765432 | M +49 172 9876543");
    assert.deepStrictEqual(r.map(n => [n.number, n.kind]), [
        ["(030) 98765432", "Telefon"],
        ["+49 172 9876543", "Mobil"],
    ]);
});

test("Doppelte Nummer in anderer Schreibweise nur einmal", () => {
    assert.deepStrictEqual(nums("Tel +49 30 1234567\nRückruf unter 030 1234567"), ["+49 30 1234567"]);
});

test("Teams-Einwahl: Nummer ohne Konferenz-ID", () => {
    assert.deepStrictEqual(nums("+49 69 365052397,,123456789#   Germany, Frankfurt"), ["+49 69 365052397"]);
});

test("Keine Telefonnummern: Datum, Uhrzeit, IBAN, PLZ, Register, Belegnummern", () => {
    assert.deepStrictEqual(nums([
        "Termin am 01.02.2024 10:00 Uhr, 09:30 - 10:30",
        "IBAN: DE89 3704 0044 0532 0130 00",
        "IBAN DE89370400440532013000",
        "10115 Berlin",
        "Amtsgericht Charlottenburg HRB 123456",
        "USt-IdNr.: DE 123456789",
        "Rechnungsnummer 0012345678",
        "Bestellung Nr. 0098765432",
        "Version 0.8.1 vom 2026-10-13",
    ].join("\n")), []);
});

test("Zu kurz / zu lang", () => {
    assert.deepStrictEqual(nums("Durchwahl 0-12\nNummer 0123456789012345678"), []);
});

test("Vergleichsform wie in brandline", () => {
    assert.strictEqual(Extract.canonical("+49 (0) 30 123"), "030123");
    assert.strictEqual(Extract.canonical("0049 30 123"), "030123");
    assert.strictEqual(Extract.canonical("+1 212 555 0100"), "0012125550100");
});

console.log(failed ? `${failed} Test(s) fehlgeschlagen` : "Alle Tests bestanden");
process.exitCode = failed ? 1 : 0;
