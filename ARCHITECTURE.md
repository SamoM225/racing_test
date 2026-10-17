# Architektúra a fyzika – RC Racing

Dokument pre programátora. Vysvetľuje, ako hra funguje, aké modely a vzorce používa a kde sa čo nastavuje. Hodnoty uvedené pri vzorcoch sú predvolené. Platné hodnoty sú v kóde, v triedach `CarTuning`, `ControlTuning`, `SurfaceDef` a v poliach `RaceManager`, a väčšina sa dá meniť v Inspectore na objekte `Race`.

## 1. Vrstvy

Hráč aj boti idú presne tou istou cestou. Jediný rozdiel je, odkiaľ prichádza vstup.

```
PlayerDriver (prst / myš)  ┐
                           ├─> DriverInput ──> AutoPilot ──> CarController ──> Unity PhysX
AIDriver (virtuálny palec) ┘   (joystick)      (riadenie,     (odpruženie,
                                               rýchlosť)      pneumatiky, nárazy)
                    RaceManager: poradie, rubber-band, body, respawn, krok simulácie
                    Track: geometria, ideálna stopa, rýchlostný profil
```

- **`DriverInput`** je virtuálny joystick z racing_unity. Obsahuje `side` (posun naprieč traťou), `boostBrake` (−1 brzda … +1 boost), `dash` (smer švihu) a `holding` (prst na obrazovke). Vodič auto nikdy neriadi priamo.
- **`AutoPilot`** je jazdná logika hry. Riadi auto po ideálnej stope (pure pursuit) a vstup len posúva navigačný bod, škáluje rýchlosť a spúšťa dash. Rieši pravidlá joysticku, rubber-band a otáčanie pri zaseknutí.
- **`CarController`** je fyzika auta. Pozná len `steer` (−1…1), `throttle` (−1…1) a boost.
- **`RaceManager`** vlastní preteky a jeden deterministický krok simulácie.
- **`Track`** postaví trať z máp racing_unity a vopred vypočíta ideálnu stopu a rýchlostný profil.

| Súbor | Obsah |
|---|---|
| `CarTuning.cs` | všetky fyzikálne normy auta (SI jednotky) |
| `ControlTuning.cs` | ovládanie, energia, dash (hodnoty racing_unity) |
| `Surfaces.cs` | povrchy, `DriverInput`, `IDriver`, `CarState`, pomocné materiály |
| `CarController.cs` | fyzika auta, nárazy, poškodenie, vizuál auta |
| `AutoPilot.cs` | jazdná logika: riadenie, cieľová rýchlosť, plyn, dash, otáčanie |
| `AIDriver.cs` | boti: vnímanie, rozhodnutia, palec |
| `PlayerDriver.cs` | dotyk a myš: joystick po 0,8 s, švih = dash |
| `RaceManager.cs` | tok pretekov, krok simulácie, pozície, kolá, checkpointy, slipstream |
| `RaceManager.RubberBand.cs` | rubber-band |
| `RaceManager.Recovery.cs` | respawn, duch, zničenie, iskry |
| `RaceManager.Scoring.cs` | drift, body, koniec pretekov |
| `RaceCar.cs` | dáta jedného auta v pretekoch, body |
| `Track.cs` | trať, ideálna stopa, rýchlostný profil, steny, terén |
| `ChaseCamera.cs`, `RaceHud.cs` | kamera zhora (hodnoty `VehicleCamera`) a IMGUI rozhranie |

## 2. Jednotky a konvencie

- **Jednotky:** SI, teda m, s, kg, N, m/s, m/s². Uhly sú v stupňoch; rýchlosť otáčania (`YawRate`, `driftYawRate`) je v rad/s.
- **Krok fyziky:** `dt` = 0,02 s (50 Hz). Autá sú interpolované, obraz beží rýchlejšie.
- **Poloha na trati:**
  - `Distance` = metre od cieľovej čiary v rámci kola.
  - `Progress` = metre od štartu pretekov, súvislé cez kolá, na rošte záporné.
  - `Lateral` = metre od stredovej čiary, **+ = vpravo**.
- **`Track.Curvature`:** 1/m, **+ = zákruta doprava**.
- **`heading`** (`AutoPilot`): uhol nosa voči smeru trate, **+ = nos vpravo**.
- **`SlipAngle`:** uhol medzi nosom a smerom pohybu (uhol karosérie). `FrontSlip` a `RearSlip` sú priemerné uhly šmyku pneumatík na nápravách.
- **Determinizmus:** ten istý seed dá tie isté preteky bit po bite. Testy na tom stoja. Pri úpravách nemeň poradie operácií ani volania generátora náhodných čísel bez dôvodu.

## 3. Krok simulácie

`RaceManager.Tick(dt)` má pre každé auto v každom kroku rovnaké poradie:

1. Poloha na trati, kolá, checkpointy, cieľ (`UpdateTrackPosition`).
2. Poradie a predbiehania (`UpdatePositions`). Predbehnutie sa započíta, až keď ho auto udrží 1,5 s.
3. História stavov pre vnímanie botov (`RecordHistory`).
4. Slipstream (`UpdateSlipstream`).
5. Rubber-band zapíše `car.Handicap` (OrderSpeed).
6. Pre každé auto: `Driver.GetInput` → `AutoPilot.Step` → `CarController.Simulate`.
7. Drift body a kontrola respawnu.
8. Koniec pretekov.

Potom `Physics.Simulate` (v hre to robí Unity po `FixedUpdate`, v testoch slučka testu) a kontakty z neho spracuje `CarController.OnContacts`.

`AutoPilot.Step` postupuje takto: pravidlá joysticku → navigačný bod a rýchlostný príkaz → dash → riadenie → cieľová rýchlosť → plyn → otáčanie pri zaseknutí (ak treba) alebo `Simulate`.

`CarController.Simulate` postupuje takto: pohyb → časovače a strata priľnavosti → riadenie → motor a brzdy → štyri kolesá → sily na karosériu → bočný dash → drift a arkádová pomoc → vizuál.

## 4. Fyzika auta (`CarController`, `CarTuning`)

Auto je jedno `Rigidbody` s hmotnosťou `mass` (1200 kg). Kolesá sú raycasty, nie WheelCollidery.

### 4.1 Odpruženie

Každé koleso má jednu pružinu s tlmičom pozdĺž osi auta:

`F = k · x + c · ẋ`

- `x` = stlačenie, `ẋ` = rýchlosť stlačenia.
- `k = spring` (22 000 N/m), `c = damper` (2 600 N·s/m).
- Sila pôsobí v mieste kolesa. Je zároveň zaťažením kolesa `N` pre pneumatiku.

### 4.2 Pneumatiky

Sily sa počítajú v rovine povrchu, v súradniciach pneumatiky. Predné kolesá sú natočené o uhol riadenia.

- **Bočná sila** je sila, ktorá by v tomto kroku zrušila bočný šmyk `v_lat`, násobená tuhosťou:
  `F_lat = −v_lat · (m/4) / dt · lateralStiffness`
- **Pozdĺžna sila** = pohon (podiel nápravy podľa `driveBias`) − brzdy (`brakeFront` 0,6 / `brakeRear` 0,4) − valivý odpor povrchu (`rollingDrag · v`).
- **Kruh trenia:** `|F| ≤ μ · N`, kde `μ = grip · povrch.grip`, na zadnej náprave ešte `· rearGrip`. Keď súčet presiahne kruh, zmenší sa na jeho okraj.
- **Utrhnutie:** keď bočný šmyk prekročí `slideStart` (1 m/s), trenie plynulo klesá na kinetické (`povrch.slideFriction`, napr. 0,85) až po `slideFull` (3,5 m/s). Preto sa na ľade a hline auto šmýka a drift drží.
- **Bez prevrátenia od pneumatík:** sily pneumatík pôsobia vo výške ťažiska. Vytvárajú vybočenie, ale nie klopenie, ktoré by auto prevrátilo.

### 4.3 Prenos váhy, preťáčanie a nedotáčanie

Pozdĺžne zrýchlenie presúva zaťaženie medzi nápravami. Model ho uplatní len na priľnavosť, nie na pruženie:

`ΔN = m · a · (h/L) · weightTransfer / 2`  (na jedno koleso nápravy)

- `h/L = cogHeightRatio` (0,12), `weightTransfer` = 0,8.
- **Brzdenie** pridá zaťaženie vpredu a ubere vzadu. Zadok sa utrhne, auto **preťáča** (oversteer).
- **Plyn** pridá zaťaženie vzadu a ubere vpredu. Predok sa šmýka, auto **nedotáča** (understeer).

Rozpoznanie v kóde:
- `Oversteer`: zadok šmýka (`RearSlip` > 6°) a karoséria je vybočená (`|SlipAngle|` > 6°). Toto je drift.
- `Understeer`: predok šmýka viac ako zadok o 3° a zadok ostáva v stope (`|SlipAngle|` < 4°). `AutoPilot` pri nedotáčaní uberie plyn (na 0,1), aby predné kolesá znova chytili. Za drift sa nepočíta.

### 4.4 Motor, brzdy, aerodynamika

- **Pohon:** `a = throttle · acceleration · (1 − (v / v_top)²) · zdravie` (+ `boostAcceleration` počas boostu).
- **Najvyššia rýchlosť:** `v_top = maxSpeed · povrch.maxSpeed · (1 + draftTopSpeed · draft) · zdravie`.
- **Zdravie:** `zdravie = 1 − damageSpeedLoss · úroveň poškodenia` (−5 % za úroveň).
- **Brzdy a cúvanie:** brzda `brakeDeceleration` (24 m/s²); cúvanie `reverseAccelShare · acceleration`, najviac `reverseMaxSpeed`.
- **Prítlak:** `F = downforce · v²`, štvrtina ide navyše do kruhu trenia každého kolesa.
- **Odpor vzduchu:** `a = airDrag · v²`. V slipstreame je o `draftDragCut` (45 %) menší.

### 4.5 Arkádová pomoc

Fyzika je zámerne arkádová, aby sa auto na mobile dalo hrať.

- **Drift assist** (`driftAssist`): pri šmyku sa vektor rýchlosti pomaly stáča k nosu auta, takže drift neberie rýchlosť.
- **Ovládanie vybočenia** (`driftControl`, `driftYawRate`): v drifte riadenie určuje rýchlosť otáčania, `cieľ = steer · driftYawRate`. Bez toho by sa šmyk nedal udržať.
- **Vzpriamovanie** (`airRighting`, `airRightingDamping`): vo vzduchu a na dvoch kolesách moment vracia auto kolesami dole. Malé RC autá by inak pristávali na streche.
- **Prevrátenie späť** (`FlipBack`): auto na boku alebo streche, ktoré stojí aspoň 0,6 s, poskočí a pretočí sa na kolesá.

### 4.6 Drift a mini-turbo

- Drift = `Oversteer` nad 9 m/s s uhlom karosérie nad `driftAngle` (10°), pod 100° (nad 100° sa auto točí).
- Bočný dash ani náraz drift nie sú.
- Drift nabíja energiu.
- Koniec driftu po 0,8 / 1,6 / 2,6 s dá mini-turbo úrovne 1 / 2 / 3, čiže `miniTurboPerTier` sekúnd boostu za úroveň.

### 4.7 Nárazy a poškodenie

Kontakty prichádzajú z `Physics.ContactEvent`. Na rozdiel od `OnCollisionEnter` chodí aj v testoch editora.

- **Náraz = špička impulzu**, nie „enter“. Pri špekulatívnych kontaktoch prichádza „enter“ o krok skôr s nulovým impulzom.
- **Rýchlosť nárazu** sa počíta z impulzu `J`:
  `v_n = J / (m_red · (1 + e))`, kde `m_red = m₁m₂/(m₁+m₂)` (pri stene len `m`) a `e = 0,15` je odrazivosť karosérie.
- **Náraz auto–auto (knock)** od `knockMinSpeed` (1,5 m/s):
  - Pneumatiky sa na 0,15–0,5 s uvoľnia (bočná priľnavosť −30 %, kruh trenia −40 %). Auto sa šmykne s impulzom, ktorý dostalo od PhysX, a nevracia sa na starú stopu.
  - Úder mimo stredu roztočí auto: `ω = rameno × smer nárazu · v_n · knockSpin`, najviac `knockSpinMax`.
  - Riadenie počas šmyku drží rýchlosť otáčania, aby sa auto neroztočilo dokola.
- **Dash-ram:** auto, ktoré práve dashuje, zasiahnuté auto odhodí rýchlosťou 3–6 m/s podľa rýchlosti priblíženia, uvoľní mu pneumatiky a zruší mu dash. Útočník sa odrazí o 30 % a na 0,2 s sa šmykne.
- **Stena:** odraz rieši PhysX, k tomu iskry, otras kamery a poškodenie. Stena auto zámerne neroztočí, roztočenie pri stene by viedlo k ďalšiemu nárazu.
- **Poškodenie (HP 100):**

  | Zdroj | Poškodenie |
  |---|---|
  | auto–auto | `(v_n − knockDamageFrom) · knockDamagePerMs`, od 3 m/s, 2 HP za m/s |
  | dash-ram | zasiahnuté auto `ramDamageBase + sila` (6–9 HP), útočník `ramSelfDamage` (1 HP) |
  | stena | `(v_n − wallDamageFrom) · wallDamagePerMs`, od 5 m/s |

  Úrovne: pod 75 HP dym, pod 50 odletí predné krídlo, pod 25 zadné krídlo a oheň. Pri 0 HP je auto zničené a v ďalšom kroku sa respawne opravené.

### 4.8 Bočný dash

Navigačný bod skočí o `dashSideDistance` šírok auta (2×) a auto fyzicky prejde presne túto vzdialenosť za `dashSideTime` (0,5 s):

- Bočná rýchlosť klesá lineárne (ako v racing_unity), takže `v_peak = 2 · vzdialenosť / čas`.
- P-regulátor ťahu túto rýchlosť dorovnáva: zisk 15 1/s, najviac 60 m/s².
- Pneumatiky na začiatku stratia 85 % bočnej priľnavosti a strata postupne slabne.
- Riadenie počas dashu len drží auto rovnobežne s traťou.

## 5. Trať (`Track`)

- **Geometria:** stredová čiara je Bézierova krivka z mapy racing_unity (s prevýšením a mostmi), prevzorkovaná každé ~2 m (`Step`). Šírku násobí `roadWidthScale`. Steny sú vo vzdialenosti polovica šírky + výbeh.
- **Ideálna stopa:** elastická páska. Každý bod sa ťahá k stredu medzi susedmi, v mierkach 24 / 12 / 6 / 3 / 1 vzoriek, a ostáva na ceste. Vyrovnaná stopa = najväčší polomer = najrýchlejšia zákruta.
- **Rýchlostný profil** (`LineSpeed`, `LimitSpeed`):
  1. **Zákruta:** `v = √(μg/κ)`, kde `κ` je krivosť kružnice cez tri body stopy a `μ = grip · povrch · 0,85` (bez prítlaku, s rezervou na šmyk).
  2. **Hrebeň:** `v = √(gR)`. Nad touto rýchlosťou by auto na vypuklom hrbole odletelo.
  3. **Strop:** `topSpeed · povrch.maxSpeed`, aspoň 6 m/s.
  4. **Brzdný prechod dozadu a zrýchľovací dopredu:** `v² ≤ v_ďalší² + 2as`, s 80 % brzdenia a 85 % zrýchlenia.
- **Dva profily:**
  - `LineSpeed` je strop `cruiseSpeed` (33 m/s), z neho jazdí autopilot.
  - `LimitSpeed` je fyzikálne maximum auta. Dobiehanie z rubber-bandu ho nikdy neprekročí.

## 6. Jazdná logika (`AutoPilot`, `ControlTuning`)

- **Pravidlá joysticku** (racing_unity):
  - Dotyk kratší ako `dashWindow` (0,8 s) je len švih (dash) a nič nestojí.
  - Potom sa ukáže joystick. Prvú sekundu je zadarmo, potom míňa 0,5 energie/s.
  - Pustenie viditeľného joysticku ho zamkne na 3 s.
  - Pri hráčovi rieši okno 0,8 s aj `PlayerDriver`, takže švih nahor nedá boost.
- **Navigačný bod:** ideálna stopa + `NavOffset`. `side` ho posúva rýchlosťou `maneuverSideSpeed`; bod ostáva meter od okraja cesty.
- **Riadenie, pure pursuit:**
  - Uhol riadenia `δ = atan(2L · sin α / d)`.
  - `L` = rázvor, `α` = uhol k bodu, `d = 6 + 0,5 s · v`, obmedzené na 7–26 m.
  - Hra kontruje šmyk nad 8° (45 % uhla šmyku).
  - Auto otočené o viac ako 90° od smeru trate zatočí naplno na kratšiu stranu a ide najviac 6 m/s. Pure pursuit by tu nefungoval (sin 180° = 0).
- **Cieľová rýchlosť:**
  `v_cieľ = profil · handicap · rýchlostný príkaz (+ mini-turbo ×1,2, + dash)`
  - **Profil** = `LineSpeed` 0,35 s dopredu · `cornerMargin` (0,9), najviac `cruiseSpeed`.
  - **Mimo ideálnej stopy** sa profil zníži o faktor `√(R_auto / R_stopa)`: vnútro zákruty je tesnejšie a pomalšie.
  - **Handicap** sa uplatní ako `(OrderSpeed + 1) / 2`.
  - **Dobiehanie** je obmedzené na `LimitSpeed`, s rezervou do 1,1× na priľnavých povrchoch.
  - **Boost a dobiehanie sa nesčítajú.** Platí rýchlejšie z nich.
  - **Spodná hranica** je 4 m/s, rubber-band auto nikdy nezastaví.
- **Plyn** je regulátor rýchlosti s chyba = cieľ − rýchlosť:
  - Pri kladnej chybe `plyn = 0,25 + 0,3 · chyba`.
  - Do −1,5 m/s auto len zvoľňuje.
  - Ďalej brzdí, ale najviac `0,7 · μg` (aby ostala priľnavosť na zatáčanie) a podľa času do zrážky s autom vpredu.
- **Otáčanie na tri razy:** auto, ktoré je 0,8 s zaseknuté (stojí, hoci dáva plyn) alebo otočené naopak, cúva s kolesami natočenými tak, aby sa nos točil k smeru trate (najviac 1,6 s), potom ide dopredu na plný rejd (najviac 1,5 s). Ak treba, celé sa to zopakuje.

## 7. Rubber-band (`RaceManager.RubberBand.cs`)

Vychádza z módu 3 „From Player“ v racing_unity. Každý krok zapíše každému autu `OrderSpeed`:

- **Autá pred hráčom** majú podľa poradia 0,9 / 0,75 / 0,5 / 0,4, **za hráčom** 1,1 / 1,2 / 1,3 / 1,4.
- **Plynulé prikladanie podľa vzdialenosti od hráča:** plne pri 25 m, potom plynulo do `frontGapSpeed` 0,4 / `rearGapSpeed` 2,0 v pásme 10–40 m. V hre sa hodnota mení skokom pri 100 m.
- **Predikcia:** medzera sa berie 1,5 s dopredu (`medzera + rýchlosť jej zmeny · 1,5`, zmena najviac ±8 m/s). Pás tak zareaguje hneď, ako sa niekto začne vzďaľovať.
- **Hráč** pribrzdí pri náskoku 8–50 m až na 0,5 (efektívne −25 %). Keď je posledný a zaostáva 15–50 m, dostane až 1,4 (+20 %).
- **Final sprint:** od 85 % pretekov bot nie je nikdy brzdený.
- **V `AutoPilot`:** `SmoothDamp` so `smoothTimeForSpeed` (1 s). Počas skutočného manévru (páčka mimo stredu) je hodnota zmrazená najviac 2 s.

## 8. AI (`AIDriver`)

Bot je hráč s virtuálnym palcom na tom istom joysticku.

- **Vnímanie:** vidí stav pretekov oneskorený o vlastný reakčný čas (log-normálny, 0,1–1,2 s) a predĺžený rýchlosťou, ako by to odhadol človek.
- **Rozhodnutia** prichádzajú každých 0,18–0,35 s:
  - **Pozícia:** predbiehanie, slipstream, obrana alebo vlastná stopa. Strany predbiehania majú skóre: blízko ideálnej stopy, vnútro ďalšej zákruty, voľný pruh. Rozhodnutie platí 2–4 s a mení sa len pri o 25 % lepšej možnosti.
  - **Boost** sa drží na rovinkách a púšťa pred brzdným bodom (`BoostIsSafe`: `s = (v² − v_povolená²)/2a + reakčná dráha`). Raz za brzdnú zónu si bot „hodí kockou“, či tentoraz nepustí boost neskoro.
  - **Brzda:** odstup ~3 m + 0,8 s rýchlosti približovania od auta vo vlastnom pruhu.
  - **Dash** má rozpočet na kolo. V poradí: dokončiť predbehnutie, odtlačiť auto vedľa, naraziť zozadu, brzdný test na auto za sebou, minúť energiu, ktorá by pretiekla.
- **Palec:** krátke ťuknutia s mŕtvou zónou, zmena pruhu ~4 m/s, vlastná stopa + pomalé vlnenie, necháva miesto autu vedľa.
- **Charakter:** 5 archetypov (Racer, Bully, Speedster, Tactician, Rookie), zručnosť podľa obtiažnosti a ±12 % osobný rozptyl.

## 9. Body

Body za preteky:

- **umiestnenie:** 200 / 150 / 120 / 95 …
- **checkpointy:** pool 200, podľa poradia príjazdu 100 / 80 / 50 / 30 %. Checkpoint dá aj energiu.
- **drift:** `|uhol| · rýchlosť · 0,5` za sekundu, s komboom do ×5, delené 20, najviac 250.
- **predbiehania:** 15 za kus, najviac 150.
- **dash zásahy:** 10 za kus, najviac 100.
- **najrýchlejšie kolo:** 50.
- **čistá jazda:** 100 − 10 za náraz do steny.
- **dokončenie:** 100.

V cieli sa ďalej ukazuje rozdané poškodenie a počet zničení.

## 10. Testy

V menu Unity **Racing**:

- **Run Rubber Band Test:**
  - Fyzikálne kontroly:
    - bočný dash prejde svoju dráhu;
    - ram odhodí a poškodí zasiahnuté auto;
    - auto sa otočí z polohy naopak a od steny;
    - auto sa prevráti zo strechy späť na kolesá;
    - plyn vyvolá nedotáčanie, brzda preťáčanie.
  - Potom 3 mapy × 3 typy hráča × 5 pretekov s metrikami súdržnosti poľa.
- **Run Headless Bot Race (all maps):** geometria 48 tratí a jeden závod botov na každej z 24 máp.
- **Build Android APK:** APK do `Builds/`.

Testy bežia v batch móde (`-executeMethod Racing.EditorTools.RubberBandTest.Run`, resp. `RacingEditorTools.RunBotRace`). `RACING_MAPS=…` obmedzí mapy, `RACING_TRACE=1` vypíše priebeh, incidenty a poškodenie. Keďže preteky sú deterministické, úprava, ktorá nemá meniť správanie, musí dať rovnaké čísla bit po bite.
