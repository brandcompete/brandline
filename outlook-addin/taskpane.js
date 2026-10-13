// brandline für Outlook: Telefonnummern aus der geöffneten Nachricht / dem Termin anzeigen und per
// brandline:-Link an die installierte App übergeben (die App wählt sofort).
//
// Warum ein Link und kein Aufruf an localhost? Edge/WebView2 sperrt seit Version 143 Anfragen von
// Webseiten (also auch Add-ins) an localhost („Local Network Access“), ohne dass ein Add-in das
// freischalten kann. Ein Link-Schema geht über Windows und ist davon nicht betroffen.

"use strict";

// ---------------- Nummern erkennen (ohne Office, auch für Tests unter Node) ----------------

const Extract = (() => {
    // Kandidat: beginnt mit +, 00, 0 oder (0 – so beginnen deutsche und internationale Rufnummern.
    // Danach Ziffern mit üblichen Trennzeichen. Interne Kurzwahlen werden bewusst nicht erkannt.
    const candidate = /(?:\+|\(0|0)[\d\s \-\/().]{5,}\d/g;

    // Text vor der Nummer (gleiche Zeile), der auf die Art schließen lässt.
    const kinds = [
        { kind: "Fax", re: /\b(fax|telefax)\b/i },
        { kind: "Mobil", re: /\b(mobil|mobile|handy|cell|mob|m)\b\.?\s*[:.]?\s*$/i },
        { kind: "Mobil", re: /\b(mobil|mobile|handy|cell)\b/i },
        { kind: "Telefon", re: /\b(tel|telefon|phone|fon|t|durchwahl|zentrale)\b/i },
    ];

    // Davor steht etwas, das keine Telefonnummer ist (Bankdaten, Register, Belegnummern …).
    // Wortanfang genügt („Rechnungsnummer“, „Bestellung Nr.“).
    const notPhone = /\b(iban|bic|blz|konto|ust|steuer|st\.?-?nr|hrb|hra|amtsgericht|kunden|rechnung|bestell|auftrag|order|invoice|ticket|plz|ref)[^\n]{0,20}$/i;

    function digitsOf(s) {
        return s.replace(/\D/g, "");
    }

    /** Vergleichsform wie in brandline: +49/0049 → 0, nur Ziffern. */
    function canonical(number) {
        let n = number.trim();
        if (n.startsWith("+")) n = n.replace("(0)", "");
        n = n.replace(/^\+/, "00").replace(/\D/g, "");
        if (n.startsWith("0049")) n = "0" + n.slice(4);
        return n;
    }

    // Enthält ein Datum („01.02.2024 10“ aus „01.02.2024 10:00 Uhr“)
    function looksLikeDate(s) {
        return /(^|\D)\d{1,2}[./]\d{1,2}[./](\d{4}|\d{2})(\D|$)/.test(s)
            || /\d{4}-\d{2}-\d{2}/.test(s);
    }

    /**
     * @param {string} text Klartext der Nachricht
     * @returns {{number: string, kind: string, context: string}[]}
     */
    function numbers(text) {
        const found = new Map();
        for (const line of text.split(/\r?\n/)) {
            candidate.lastIndex = 0;
            let m;
            while ((m = candidate.exec(line)) !== null) {
                // Am Ende angehängte Klammern/Trennzeichen abschneiden, z. B. "030 123 (Zentrale"
                let raw = m[0].replace(/[\s \-\/(.]+$/, "");
                // Öffnende Klammer ohne schließende am Anfang: "(030) 123" bleibt, "(0)…" auch.
                const digits = digitsOf(raw);
                if (digits.length < 6 || digits.length > 16) continue;
                if (looksLikeDate(raw)) continue;
                // Mitten in einer längeren Ziffernfolge (z. B. IBAN „DE89 3704 …“) → keine Telefonnummer
                const before = line.slice(0, m.index);
                const after = line.slice(m.index + m[0].length);
                if (/[A-Za-z0-9]$/.test(before) || /^[A-Za-z]/.test(after)) continue;
                if (notPhone.test(before)) continue;

                const key = canonical(raw);
                if (found.has(key)) continue;

                const prefix = before.slice(-25);
                const kind = (kinds.find(k => k.re.test(prefix)) || { kind: "" }).kind;
                found.set(key, { number: raw.trim(), kind, context: line.trim() });
            }
        }
        // Fax zuletzt
        return [...found.values()].sort((a, b) => (a.kind === "Fax") - (b.kind === "Fax"));
    }

    return { numbers, canonical };
})();

if (typeof module !== "undefined") module.exports = Extract;

// ---------------- Aufgabenbereich ----------------

/** Link, den Windows an brandline übergibt. Das + bleibt erhalten (encodeURIComponent → %2B). */
function dialUrl(number, scheme = "brandline") {
    return `${scheme}:${encodeURIComponent(number.trim())}`;
}

if (typeof Office !== "undefined") {
    Office.onReady(() => {
        document.getElementById("manual").addEventListener("submit", e => {
            e.preventDefault();
            const n = document.getElementById("manualNumber").value;
            if (n.replace(/\D/g, "").length > 0) launch(dialUrl(n));
        });
        load();
        // Angehefteter Aufgabenbereich: beim Wechsel der Nachricht neu einlesen.
        if (Office.context.mailbox.addHandlerAsync) {
            Office.context.mailbox.addHandlerAsync(Office.EventType.ItemChanged, load);
        }
    });
}

function load() {
    const item = Office.context.mailbox.item;
    const list = document.getElementById("numbers");
    list.innerHTML = "";
    document.getElementById("empty").hidden = true;
    document.getElementById("loading").hidden = false;
    if (!item) {
        document.getElementById("loading").hidden = true;
        document.getElementById("empty").hidden = false;
        return;
    }

    const isAppointment = item.itemType === Office.MailboxEnums.ItemType.Appointment;
    document.getElementById("numbersTitle").textContent =
        isAppointment ? "Telefonnummern in diesem Termin" : "Telefonnummern in dieser Nachricht";

    const person = isAppointment ? item.organizer : item.from;
    const senderSection = document.getElementById("sender");
    if (person) {
        senderSection.hidden = false;
        senderSection.querySelector(".label").textContent = isAppointment ? "Organisator" : "Absender";
        document.getElementById("senderName").textContent = person.displayName || person.emailAddress;
        document.getElementById("senderMail").textContent = person.emailAddress || "";
    } else {
        senderSection.hidden = true;
    }

    item.body.getAsync(Office.CoercionType.Text, result => {
        document.getElementById("loading").hidden = true;
        let text = result.status === Office.AsyncResultStatus.Succeeded ? result.value : "";
        if (isAppointment && item.location) text = item.location + "\n" + text;
        show(Extract.numbers(text));
    });
}

function show(numbers) {
    const list = document.getElementById("numbers");
    list.innerHTML = "";
    document.getElementById("empty").hidden = numbers.length > 0;

    for (const n of numbers) {
        const li = document.createElement("li");

        const info = document.createElement("div");
        info.className = "info";
        const number = document.createElement("div");
        number.className = "number";
        number.textContent = n.number;
        number.title = n.number;
        const context = document.createElement("div");
        context.className = "context";
        context.textContent = n.kind ? `${n.kind} · ${n.context}` : n.context;
        context.title = n.context;
        info.append(number, context);
        li.appendChild(info);

        if (n.kind !== "Fax") {
            const call = document.createElement("a");
            call.className = "call";
            call.textContent = "Anrufen";
            call.href = dialUrl(n.number);
            call.addEventListener("click", e => {
                e.preventDefault();
                launch(call.href);
            });
            li.appendChild(call);
        }
        list.appendChild(li);
    }

    buildDiagnostics(numbers.find(n => n.kind !== "Fax")?.number || "030 123456");
}

/**
 * Link öffnen. In einem Add-in ist der Aufgabenbereich ein iframe; ein neues Fenster gibt der
 * Outlook-Host an Windows weiter, das den Link der registrierten App (brandline) übergibt.
 */
function launch(url) {
    const w = window.open(url, "_blank");
    if (!w) window.location.href = url;
}

// Zur Fehlersuche: mehrere Wege, einen Link zu öffnen – je nach Outlook-Variante greift ein anderer.
function buildDiagnostics(sample) {
    const box = document.getElementById("diag");
    const result = document.getElementById("diagResult");
    box.innerHTML = "";
    const ways = [
        ["A: neues Fenster", () => window.open(dialUrl(sample), "_blank")],
        ["B: Link (_blank)", null, dialUrl(sample), "_blank"],
        ["C: Link (_top)", null, dialUrl(sample), "_top"],
        ["D: dieses Fenster", () => { window.location.href = dialUrl(sample); }],
        ["E: tel:-Link", null, dialUrl(sample, "tel"), "_blank"],
    ];
    for (const [label, action, href, target] of ways) {
        const el = document.createElement(action ? "button" : "a");
        el.className = "diag";
        el.textContent = label;
        if (action) {
            el.type = "button";
            el.addEventListener("click", () => {
                try { action(); result.textContent = `${label}: ausgelöst`; }
                catch (err) { result.textContent = `${label}: ${err.message}`; }
            });
        } else {
            el.href = href;
            el.target = target;
            el.rel = "noopener";
            el.addEventListener("click", () => { result.textContent = `${label}: ausgelöst`; });
        }
        box.appendChild(el);
    }
}
