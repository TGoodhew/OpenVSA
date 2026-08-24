# Where the manuals and the firmware disagree

Places where a bench instrument does **not** behave as its published manual says, recorded so that
the same hour is not spent twice and so that the list can be re-run against a firmware update to see
what a newer revision fixes.

Every entry says what the manual claims, what the instrument actually did, and how the deviation was
observed. **Entries marked VERIFIED were re-measured on the date given; entries marked RECORDED come
from earlier bench work and have not been re-measured since.** The distinction matters: only a
verified entry can be compared against a later firmware revision and called fixed.

## How to re-run this list

Both instruments answer over VISA with no OpenVSA build needed:

```powershell
Add-Type -Path "C:\Program Files\IVI Foundation\VISA\Microsoft.NET\Framework64\v4.0.30319\VISA.NET Shared Components 8.0.2\Ivi.Visa.dll"
$s = [Ivi.Visa.GlobalResourceManager]::Open('GPIB0::17::INSTR', [Ivi.Visa.AccessModes]::None, 4000)
$s.RawIO.Write([Text.Encoding]::ASCII.GetBytes("*IDN?`n"))
[Text.Encoding]::ASCII.GetString($s.RawIO.Read(4096))
```

**Read `:SYSTem:ERRor?` after every probe, and `*CLS` before the next one.** A command the firmware
rejects usually returns *nothing*, so the visible symptom is a VISA timeout and the actual cause sits
in the error queue — see the cross-cutting note at the end, which is the single most useful thing on
this page.

**Do not send the manual's `[ ]` brackets.** They mark optional mnemonics, not literal text. Sending
`[:SENSe]:POWer:RF:ATTenuation:AUTO?` earns `-101 "Invalid character"` from both instruments, which
looks exactly like an unsupported command and is not one. This mistake was made while first drafting
this page.

---

## Agilent/HP E4406A VSA — the measurement front end

| | |
|---|---|
| Identity | `Hewlett-Packard,E4406A,US40062429,A.08.10    20041215  12:30:18` |
| **Firmware** | **A.08.10**, dated 2004-12-15 |
| Options | `"BAH","202","252","BAC","BAF","B7C"` |
| Manual | *E4406A VSA Series Transmitter Tester Programmer's Guide*, manufacturing part number **E4406-90303** |
| Address | `GPIB0::17::INSTR` (see `openvsa-bench-hardware` notes; a local bus scan is not trustworthy here) |

### 1. `POWer:RF:ATTenuation:AUTO` is documented without restriction and is rejected — VERIFIED 2026-08-08

**Manual**, "RF Port Input Attenuator Auto":

> `[:SENSe]:POWer[:RF]:ATTenuation:AUTO OFF|ON|0|1`
> `[:SENSe]:POWer[:RF]:ATTenuation:AUTO?`
> Select the RF input attenuator range to be set either automatically or manually.

The entry carries **no mode restriction** — no "You must be in … mode" remark, unlike neighbouring
commands that have one.

**Firmware A.08.10, in Basic mode:**

```
:SENSe:POWer:RF:ATTenuation:AUTO?      -> no reply      :SYSTem:ERRor? -> -113,"Undefined header"
:SENSe:POWer:RF:ATTenuation:AUTO ON    -> (write)       :SYSTem:ERRor? -> -113,"Undefined header"
```

`-113 Undefined header` means the parser does not know the mnemonic at all — this is not a settings
conflict or a mode restriction being enforced, it is the command being absent.

**This is a genuine manual-versus-firmware contradiction** and the best candidate on this page for a
firmware update to fix.

**Note that the parent command works**: `:SENSe:POWer:RF:ATTenuation?` answers `+0` with no error. It
is only the `:AUTO` subnode that is missing.

**What OpenVSA does instead:** leaves input ranging to the instrument and uses Basic mode's own
digitiser ranging, `:SENSe:WAVeform:ADC:RANGe` (verified to answer `AUTO`). See
`src/OpenVSA.Hal.Visa/E4406ACommands.cs`.

### 2. `POWer:RF:RANGe[:UPPer]` — restriction documented, failure mode not — VERIFIED 2026-08-08

**Manual**, "RF Port Power Range Maximum Total Power":

> `[:SENSe]:POWer[:RF]:RANGe[:UPPer] <power>` … Set the maximum expected total power level at the
> radio unit under test.
> **Remarks:** … You must be in the Service, cdmaOne, EDGE(w/GSM), GSM, NADC, PDC, cdma2000, or
> W-CDMA (3GPP) mode to use this command.

Basic mode is **not** in that list, so the restriction itself is documented — this is a **milder**
entry than the one above.

**Firmware A.08.10, in Basic mode:**

```
:SENSe:POWer:RF:RANGe:UPPer?           -> no reply      :SYSTem:ERRor? -> -113,"Undefined header"
```

**The deviation is the failure mode, not the restriction.** A documented-but-unavailable command
would reasonably answer `-221 Settings conflict` or similar and still reply. Instead the header
simply does not exist in Basic mode, the query returns nothing, and the caller sees a VISA timeout
with no indication of why. A firmware update is unlikely to change this; the mode restriction is by
design and only the diagnosis is unhelpful.

### 3. Digitisation rate is 1.5× the information bandwidth — VERIFIED 2026-08-08

Not a manual contradiction, but recorded here because it contradicts the **reasonable assumption**
(and OpenVSA's original one) that a VSA digitises at about 1.28× its information bandwidth.

```
:SENSe:WAVeform:BANDwidth:RESolution?  -> +1.00000000E+007   (10 MHz)
:SENSe:WAVeform:APERture?              -> +6.66666667E-008   (66.667 ns = 15 MHz)
```

15 MHz / 10 MHz = **1.50 exactly**.

**Never infer the sample rate from the span.** Ask `:SENSe:WAVeform:APERture?` — the relationship
between the two is this instrument's, not a law of the product. OpenVSA does exactly that.

**The 1.5× itself is one point and not a law — see entry 7.** At 5 MHz commanded the same instrument
returns 15 MS/s, a ratio of 3.0. The advice above stands; the ratio does not generalise.

### 4. `:FORMat:DATA REAL,32` is global, not per-query — RECORDED

**Observed:** setting `:FORMat:DATA REAL,32` for the I/Q trace also changes the reply to
`:FETCh:WAVeform1?`, the *scalar* results block, which then returns a binary block rather than
ASCII text. Parsing that reply as text fails on the first non-printable byte.

The guide describes the format setting without stating that it applies across the whole `FETCh`
family. **Not re-measured since it was first found** — worth re-verifying if this list is re-run.

### 5. Fewer samples returned than the requested sweep time implies — RECORDED

**Observed:** a request for 1024 samples came back with 512. The instrument may return fewer samples
than the capture length asked for, so the returned count must be read from the scalar block
(`:FETCh:WAVeform1?`, sample-count field) rather than computed from the request.

**Not re-measured since.** OpenVSA already reads the count rather than assuming it.

### 7. The 1.5× ratio of entry 3 is one point, not a law — VERIFIED 2026-08-23

Recorded separately rather than folded into entry 3, because entry 3 is accurate about what it
measured and the error is in reading a ratio off a single point.

**Measured 2026-08-23** during `OpenVSA.Verify --demod-check`, three consecutive acquisitions:

```
requested bandwidth                    5.0 MHz
:SENSe:WAVeform:BANDwidth:RESolution:ACTual?  -> 6.7 MHz     (rounded UP, not down)
sample interval from :FETCh:WAVeform1? scalar 1 -> 66.667 ns  (15.0000 MS/s)
```

15 MS/s at 5 MHz commanded is a ratio of **3.0**, and against the 6.7 MHz actually in force
**2.24**. Entry 3's 1.5× came from RBW 10 MHz → 15 MHz, which is the top of the range. **The
instrument decimates in integer steps, so the ratio is whatever the current step makes it and is not
a ratio at all.** It holds 15 MS/s — decimation by one — down to at least 5 MHz commanded.

**This contradicts `REQ-E44-002b`**, which gave the maximum sample rate as 7.5 MHz. That figure was
read off the end of a table whose widest RBW was 1 MHz; it is the maximum *of those settings*, not of
the instrument. The requirement has been corrected. **The crossover from 7.5 MS/s to 15 MS/s lies
somewhere between 1 MHz and 5 MHz commanded and has not been measured** — the two ends are known and
the step between them is not.

**What it cost, and what it did not.** Nothing measured was wrong: the front end reads the aperture
back at configure and every block carries the instrument's own answer, which is why a 500 ksym/s
signal demodulated at 30 samples a symbol and recovered 1024 of 1024 PN9 bits. What is affected is
`InstrumentLimits.EstimateSampleRate`, which interpolates linearly between zero and the measured
maximum and therefore reported **half** the true rate at a 5 MHz span. It is used to size a block
before the instrument has been configured, so its error makes captures shorter in time than intended
while still delivering the requested number of samples. It is a lower bound in this region, and
nothing should treat it as more than one.

**Fixing it properly needs a measurement that has not been made:** the actual bandwidth and aperture
across the whole RBW range, in the instrument's own steps, rather than at the six points entry 3 and
`REQ-E44-002b` between them happen to cover.

### 6. Bench environment, NOT a firmware deviation

Recorded here only so it is not mistaken for one. Transfer through the HP-IB extender on this bench
runs at roughly **2 300 samples/s**, so a block's size has to be bounded by measured throughput
rather than by the instrument's 100 s maximum sweep time. This is a property of the cabling, not of
the firmware, and no firmware update will change it.

---

## Keysight E4438C ESG — the stimulus source

| | |
|---|---|
| Identity | `Agilent Technologies, E4438C, MY45090927, C.05.85` |
| **Firmware** | **C.05.85** |
| Options | `005,403,409,420,421,422,503,602,UNJ` |
| Manual | *E4438C ESG Signal Generator SCPI Command Reference*, manufacturing part number **E4400-90506**, April 2015 |
| Address | `TCPIP0::192.168.1.85::inst1::INSTR` — **this address has moved before**, see below |

### 1. `MIN`/`MAX` is refused on the multitone tone-count query — VERIFIED 2026-08-08

**Manual** documents the parameter form `MIN|MAX` as a general convention and uses it explicitly on
other commands (for example `[:SOURce]:PULM:INTernal[1]:FREQuency:STEP[:INCRement] <frequency>MIN|MAX`).
For the multitone tone count it documents only a range:

> `[:SOURce]:RADio:MTONe:ARB:SETup:TABLe:NTONes <num_tones>` … **Range** 2–64

**Firmware C.05.85:**

```
:RADio:MTONe:ARB:SETup:TABLe:NTONes? MIN   -> no reply   :SYSTem:ERRor? -> -108,"Parameter not allowed"
:RADio:MTONe:ARB:SETup:TABLe:NTONes?       -> +5         :SYSTem:ERRor? -> +0,"No error"
```

So the plain query works and the `MIN` form does not. The manual neither promises nor denies `MIN`
here, so this is a **convention deviation** rather than a flat contradiction — but it is the kind a
firmware update could plausibly fix, and the failure is expensive (see the cross-cutting note).

**What OpenVSA does instead:** takes the documented 2–64 for this model and does not probe. See
`src/OpenVSA.TestHarness/E4438CStimulus.cs`.

### 2. The instrument's IP address is not stable — VERIFIED 2026-08-08

Not a firmware deviation, recorded because it has twice cost time. The generator moved from
`192.168.1.82` to `192.168.1.85`. A stale address fails with:

> Could not open VISA resource … **Insufficient location information or the device or resource is
> not present in the system**

which reads exactly like a powered-off instrument, and was once reported as one. **Confirm the
address before concluding the bench is off.**

### 3. `:RADio:CUSTom:SRATe? MIN|MAX` answers, and answers a different question — VERIFIED 2026-08-24

**Manual** gives the symbol-rate ceiling as a property of the *format and the filter length*, in a
table per modulation. For the 32-symbol filter length the instrument truncates to in order to reach
its higher rates:

> `GRAYQPSK, QAM4` … 4 sps–12.5 Msps  ·  `QAM16` … 8 sps–6.25 Msps

**Firmware C.05.85**, asked for both limits after setting the format and the filter, three runs, six
combinations:

```
QPSK    RootRaisedCosine  MIN 1   MAX 50000000
QPSK    Gaussian          MIN 1   MAX 50000000
QAM16   RootRaisedCosine  MIN 1   MAX 50000000
QAM16   Gaussian          MIN 1   MAX 50000000
QAM256  RootRaisedCosine  MIN 1   MAX 50000000
QAM256  Gaussian          MIN 1   MAX 50000000
```

The query answers — unlike `:NTONes? MIN` above, so this is **not** the same deviation — but it
answers with **the hardware's absolute range and not the range in force**: the same two numbers for
every format and every filter, 1 sps against the manual's 4 or 8, and 50 Msps against the manual's
12.5 or 6.25.

**Why this one matters more than it looks.** A driver that ranged a control from this query would
offer 50 Msps on QAM256 with a root-raised-cosine filter, which the manual says the instrument cannot
produce. The instrument would then do one of the things instruments do — clip, refuse, or truncate its
filter, which the manual warns changes both the filter's response and the timing of the modulated
data — and the signal measured would not be the signal asked for. The failure would appear as a
measurement disagreement, not as a rejected setting.

**What OpenVSA does instead:** declares the manual's per-format, per-filter limits in
`E4438CStimulus.MinimumSymbolRateHz` and `MaximumSymbolRateHz(filter)`, and does not use this query to
range anything. `OpenVSA.Verify --probe-modulation` keeps the probe, because the evidence that the
query cannot be trusted for this purpose is worth having in a form that can be re-run against a later
firmware.

**Confirmed working on the same runs**, since a probe that only finds fault is not much of a probe:
`QPSK` and `GRAYQPSK` both accepted and read back exactly; a symbol rate of 1 Msym/s honoured to the
symbol; `:ALPHa 0.35` honoured; `PN9` accepted and read back; `:POLarity:ALL INVerted` and `NORMal`
both applied and reported; and `:RADio:CUSTom:STATe OFF` with `:OUTPut:STATe OFF` leaving the source
quiet.

### 3a. One unexplained VISA failure, recorded because it is unexplained

The first run of the probe failed part way through with

> The resource descriptor specifies a secure connection, but the device or VISA implementation does
> not support secure connections, or security has been disabled on the device.

after `*IDN?` had already succeeded on that session. It has **not recurred in four subsequent runs**
of the same command, so it is not reproduced and not root-caused. Recorded rather than dismissed
because the message is misleading — nothing in `TCPIP0::192.168.1.85::inst1::INSTR` asks for a secure
connection — and because the next person to see it should know it has been seen before and that the
session recovered on its own. `E4438CStimulus.Connect` already does `session.Clear()` and `*CLS`
before anything else, which is the standard remedy for a session left dirty by a previous program and
may be why it did not return.

---

## The cross-cutting note, and the most useful thing here

**A command the firmware rejects returns no reply, so the visible symptom is a timeout — and the
error stays in the queue to be blamed on something else.**

Both instruments do this, and it has produced a wrong diagnosis on each of them:

1. The query is sent. The parser rejects it and queues an error.
2. **Nothing is written back**, so the read times out. The caller sees a timeout, not a rejection.
3. Catching the timeout does **not** clear the instrument's error queue — the exception is on the
   controller side and the queue is on the instrument side.
4. The **next** unrelated command reads `:SYSTem:ERRor?`, finds the stale error, and reports it
   against itself. On this bench that produced *"-108 Parameter not allowed while setting the
   carrier"* on a scenario that had done nothing wrong.

**Therefore:**

- Any tolerated capability probe must `*CLS` behind it, or it poisons a later check.
- An error check should drain the queue **to the end**, not read a single entry — a queue is a queue.
  `E4438CStimulus.ThrowOnInstrumentError` now does this.
- A timeout on a query is a *symptom*. Read the error queue before concluding the instrument is
  absent, busy or broken.

---

## When a firmware update is tried

Re-run the verified probes above and record the result here rather than editing the entries away —
the value of this page is the comparison between revisions. Expected outcomes, in order of how
likely they are to change:

| Entry | Instrument | Likely fixed by newer firmware? |
|---|---|---|
| `ATTenuation:AUTO` undefined in Basic | E4406A A.08.10 | **Best candidate** — documented with no restriction and simply absent |
| `NTONes? MIN` refused | E4438C C.05.85 | Plausible — a convention gap rather than a design decision |
| `RANGe:UPPer` unhelpful in Basic | E4406A A.08.10 | Unlikely — the restriction is by design; only the diagnosis is poor |
| Rejections return no reply | both | Unlikely — this is IEEE 488.2 behaviour, not a bug |
| 1.5× digitisation | E4406A A.08.10 | No — an instrument property, not a defect |

Note that E4406A firmware A.08.10 dates from **2004** and the instrument is long discontinued, so a
newer revision may not exist. The E4438C manual on file is the **April 2015** edition, which is
later than firmware C.05.85 — so for that instrument the manual may be describing a revision newer
than the one installed, which is itself a plausible explanation for entry 1.
