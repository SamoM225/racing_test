# RC Racing – racing_unity minihra, prerobená a vylepšená

Unity 6000.3.23f1, built-in pipeline, bez externých balíkov, mobil na výšku. Otvor `Assets/Scenes/Race.unity`, v Game view nastav rozlíšenie na výšku (napr. 1080×1920) a stlač Play. Hlavná obrazovka má len ŠTART, NASTAVENIA a KONIEC. V nastaveniach je mapa, smer jazdy, počet súperov, obtiažnosť, **veľkosť áut** (0,5–1×, predvolene 0,7× = RC), **šírku trate** (1–2×, predvolene 1,5×) a **povrch** (podľa mapy / blato / hlina / štrk / piesok / sneh / ľad / asfalt).

## Čo je prevzaté z racing_unity (jadro hry)
- **24 máp** s presnou geometriou z `FinalRaceMaps/*.unity`: Bézierova stopa s prevýšením a mostmi, šírka, výbeh, povrchy po metroch, checkpointy, kolá, štartový rošt a hmla. Každá mapa má aj opačný smer jazdy (`_T2`), ako v hre. Dáta sú v `Assets/Racing/Resources/maps.json`.
- **Ovládanie**: auto riadi hra po stope.
  - Rýchly švih je dash, nič iné (žiadny boost počas švihu).
  - Až keď prst drží dlhšie než 0,8 s, objaví sa joystick: hore = boost, dole = spomalenie, do strán = posun.
  - Platia pravidlá joysticku: 1 s zadarmo, potom míňa energiu, po pustení 3 s zámok.
- **Kamera**: hodnoty z `VehicleCamera`, teda výška 30, vzdialenosť −40, sklon 50°, FOV 60, pohľad 20 m pred auto.
- **Rubber-band** vychádza z módu 3 „From Player“ s hodnotami zo servera:
  - autá pred hráčom 0,9 / 0,75 / 0,5 / 0,4, za ním 1,1 / 1,2 / 1,3 / 1,4,
  - medzera nad 100 m → 0,4 / 2,0,
  - SmoothDamp 2 s, vzorec (x+1)/2,
  - zmrazenie počas manévru, final sprint od 85 %.

  Hra má jediný rubber-band, ktorý zachováva hodnoty módu 3 podľa poradia. Zmeny oproti hre:
  - Hodnoty sa prikladajú plynule podľa vzdialenosti od hráča: plne pri 25 m a hodnoty 0,4 / 2,0 v pásme 10–40 m, bez skokov.
  - Pracuje s medzerou o 1,5 s dopredu, takže reaguje hneď, ako sa niekto začne vzďaľovať, nie až keď je medzera otvorená. SmoothDamp trvá 1 s.
  - Vedúci hráč pribrzdí, efektívne až o −25 % pri 50 m náskoku. Zaostávajúci hráč dostane pomoc +20 %.
  - Dobiehanie v zákrute ide najviac po hranicu priľnavosti a boost sa s dobiehaním nesčíta.
  - Boti pri zaostávaní nad 15 m dobiehajú boostom a dashom. Zabrzdení boti boostom neutekajú.

  Overenie: `Racing → Run Rubber Band Test`.
- **Pravidlá**: 4 autá, 3 checkpointy na kolo s bodmi 10/8/5/3 podľa poradia, dash za 1 energiu s cooldownom 1 s, preteky končia dojazdom hráča.

## Čo je vylepšené
- **AI** (`AIDriver`) rieši zdokumentované slabiny ich botov (`Docs/BotTesting`):
  - vlastná stopa každého bota, žiadne „vláčiky“; zmena pruhu najviac ~4 m/s, nie trhnutie k okraju,
  - predbiehanie s výberom strany a kontrolou voľného miesta, vnútorná stopa vlastní zákrutu, nechá miesto autu vedľa,
  - odstup ~3 m namiesto narážania, slipstream, obrana jedným pohybom,
  - fázy pretekov: štart (žiadne dashe prvé 3–6 s), tlak pred checkpointom, posledné kolo, final sprint,
  - dash s rozpočtom na kolo, s nádychom a rovnakou cenou ako pre hráča,
  - vnímanie s reakčným časom a odhadom pohybu, 5 typov jazdcov a rozptyl.
- **Fyzika**: povrchy sa reálne šmýkajú (pneumatiky s kruhom trenia), drift dáva body a mini-turbo. Dobiehanie z rubber-bandu nikdy nevytlačí auto nad priľnavosť zákruty a má minimálnu rýchlosť (nezastaví auto na 0, čo bola chyba na USA Mud).
- **Side-dash** je skutočný bočný výpad, nie teleport ani zatočenie:
  - Auto prejde 2 šírky za 0,5 s. Bočná rýchlosť lineárne klesá ako v racing_unity a presne toľko sa posunie aj navigačný bod, takže ho hra nemusí dorovnávať.
  - Pneumatiky počas výpadu šmýkajú, nos sa mierne natočí do smeru a karoséria sa nakloní.
- **Nárazy** sa počítajú zo skutočného impulzu kontaktu:
  - Zasiahnuté auto sa na chvíľu šmýka. Úder mimo stredu ho natočí a hra ho nevracia na starú stopu.
  - Dash-ram odhodí zasiahnuté auto, zruší mu dash a útočník sa trochu odrazí.
  - Pri náraze lietajú iskry a kamera sa otrasie.
- **Preťáčanie a nedotáčanie**: brzda a plyn prenášajú váhu medzi nápravami.
  - Pri brzdení sa odľahčí zadok, ten vybehne a auto preťáča. To je drift a dáva body.
  - Pri plyne alebo príliš rýchlom nájazde sa odľahčí predok, auto nedotáča a ide do širšieho oblúka. Hra pri tom uberie plyn a nedotáčanie sa za drift nepočíta.
  - HUD ukazuje OVERSTEER / UNDERSTEER.
- **Poškodenie (HP 100)**: nárazy uberajú podľa skutočnej prudkosti. Dotyk nestojí nič, tvrdá zrážka ~10 HP, dash-ram 6–9 HP podľa sily (útočník 1 HP), čelný náraz do steny ~15 HP. Úrovne poškodenia:

  | Úroveň | HP | Prejav |
  |---|---|---|
  | Poškodené | pod 75 | dym, −5 % výkonu |
  | Ťažko poškodené | pod 50 | odletí predné krídlo, −10 % |
  | Kritické | pod 25 | odletí zadné krídlo, oheň, −15 % |

  Pri 0 HP je auto zničené (WRECKED) a vráti sa na trať opravené. V cieli sa ukazuje rozdané poškodenie a počet zničení.
- **Recover**:
  - Auto nosom v stene alebo otočené naopak sa otočí na tri razy: cúva s kolesami natočenými tak, aby sa nos točil k smeru trate, potom ide dopredu na plný rejd.
  - Auto otočené počas jazdy (prešmyknuté) sa otočí na kratšiu stranu pomaly.
  - Auto na boku alebo na streche sa samo postaví na kolesá.
  - Ak nič z toho nepomôže, príde respawn. Hráč má pri problémoch tlačidlo RECOVER.
  - Po respawne je auto 2 s „duch“ (bliká a prechádza cez ostatné).
- **Plynulosť**: interpolujú sa všetky autá (fyzika beží na 50 Hz) a kamera mieri podľa interpolovanej polohy, preto obraz nesekne.
- **Body**: umiestnenie, checkpointy, drift, predbiehania, zásahy dashom, najrýchlejšie kolo, čistá jazda, dokončenie.

## Ladenie
Objekt `Race` (`RaceManager`): mapa, rubber-band (hodnoty), **Control** (ovládanie a energia), **Car Tuning** (všetky fyzikálne normy auta s jednotkami), **Surfaces**.

Princípy, vzorce, jednotky a poradie kroku simulácie pre programátora sú v [ARCHITECTURE.md](ARCHITECTURE.md).

## Test
`Racing → Run Headless Bot Race (all maps)` skontroluje geometriu všetkých 48 tratí (dĺžka sa musí zhodovať s hrou) a odjazdí jeden závod botov na každej z 24 máp s aktívnym rubber-bandom. Vypíše poradie, časy kôl, predbiehania, dashe, respawny a čas mimo cesty.
