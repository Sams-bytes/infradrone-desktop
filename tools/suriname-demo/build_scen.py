import json
G = json.load(open('scen_geo.json'))

def T(en, nl): return {'en': en, 'nl': nl}

common = {
  'aircraft': {
    '_note': 'Set endurance_min to the REAL endurance of your aircraft with payload. If a ferry flight needs more than 80% of it, the demo automatically adds a landing and battery swap at a midway town.',
    'name': T('VTOL fixed-wing drone (Loong 2160 class)', 'VTOL-vleugeldrone (Loong 2160-klasse)'),
    'cruise_ms': 20, 'vtol_climb_ms': 2.5, 'vtol_alt_m': 40, 'cruise_alt_m': 120, 'search_alt_m': 120, 'confirm_alt_m': 80,
    'endurance_min': 150, 'reserve_pct': 20
  },
  'ui': {
    'brand': 'DAMbv · InfraDrone Command',
    'simulation': T('SIMULATION · scenario exercise', 'SIMULATIE · scenario-oefening'),
    'sim_sensor': T('SIMULATED SENSOR VIEW', 'GESIMULEERD CAMERABEELD'),
    'press_next': T('Press → to continue', 'Druk op → om verder te gaan'),
    'chapter': T('Chapter', 'Hoofdstuk'),
    'aircraft': T('AIRCRAFT', 'TOESTEL'),
    'events': T('EVENTS', 'GEBEURTENISSEN'),
    'phase': T('Phase', 'Fase'), 'alt': T('Altitude', 'Hoogte'), 'speed': T('Speed', 'Snelheid'),
    'heading': T('Heading', 'Koers'), 'battery': T('Battery', 'Batterij'), 'flight_time': T('Flight time', 'Vliegtijd'),
    'from_base': T('From base', 'Van basis'), 'datalink': T('Datalink', 'Datalink'), 'gps': T('GPS', 'GPS'),
    'geofence': T('Geofence', 'Geofence'), 'active': T('active · stays in Suriname', 'actief · blijft in Suriname'),
    'time_x': T('time ×', 'tijd ×'), 'mission_time': T('Mission time', 'Missietijd'),
    'phases': {
      'ground': T('On ground', 'Aan de grond'), 'vtol_up': T('Vertical take-off', 'Verticale start'),
      'transition': T('Transition', 'Transitie'), 'cruise': T('Cruise', 'Kruisvlucht'),
      'patrol': T('Patrol', 'Patrouille'), 'observe': T('Observing contact', 'Contact observeren'),
      'search': T('Search pattern', 'Zoekpatroon'), 'confirm': T('Confirming', 'Bevestigen'),
      'rtb': T('Return to base', 'Terug naar basis'), 'approach': T('Approach', 'Nadering'), 'vtol_down': T('Vertical landing', 'Verticale landing'),
      'swap': T('Battery swap', 'Batterijwissel')},
    'checks': [T('GPS lock — 14 satellites', 'GPS-ontvangst — 14 satellieten'), T('Datalink', 'Datalink'),
               T('Battery 100%', 'Batterij 100%'), T('Weather within limits', 'Weer binnen limieten'),
               T('Geofence loaded — Surinamese airspace', 'Geofence geladen — Surinaams luchtruim'),
               T('Flight corridor approved (CASAS) — simulated', 'Vliegcorridor goedgekeurd (CASAS) — gesimuleerd')],
    'legend': {'border': T('International border', 'Landsgrens'), 'geofence': T('Geofence (300 m inside)', 'Geofence (300 m binnen grens)'),
               'route': T('Planned route', 'Geplande route'), 'flown': T('Flown track', 'Gevlogen spoor'),
               'search': T('Search area', 'Zoekgebied'), 'covered': T('Searched', 'Doorzocht')},
    'report': {
      'title': T('Mission report', 'Missierapport'), 'flights': T('Flights', 'Vluchten'), 'distance': T('Distance flown', 'Gevlogen afstand'),
      'flight_time': T('Flight time', 'Vliegtijd'), 'border': T('Border patrolled', 'Grens gepatrouilleerd'),
      'area': T('Area searched', 'Gebied doorzocht'), 'alerts': T('Alerts sent', 'Meldingen verstuurd'),
      'found': T('Missing person found after', 'Vermiste persoon gevonden na'), 'timeline': T('Timeline', 'Tijdlijn'),
      'print': T('Print / save as PDF', 'Afdrukken / opslaan als PDF'),
      'note': T('All positions, times and images are logged and exportable as evidence. Data stays on Surinamese systems.',
                'Alle posities, tijden en beelden worden vastgelegd en zijn exporteerbaar als bewijs. Data blijft op Surinaamse systemen.')},
    'credits': 'Boundaries © OpenStreetMap contributors (ODbL), geoBoundaries · Offline imagery: Sentinel-2 cloudless 2016 by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016), CC BY 4.0'
  }
}

# Shared chapter texts; {placeholders} are filled per scenario.
chapters = [
 {'id': 'briefing', 'title': T('Mission briefing', 'Missiebriefing'),
  'captions': [
   T('Suriname has about 1,700 km of land border — most of it rivers and dense rainforest.', 'Suriname heeft ongeveer 1.700 km landsgrens — grotendeels rivieren en dicht regenwoud.'),
   T('From the ground, these borders are very hard to watch. From the air, it becomes possible.', 'Vanaf de grond is die grens nauwelijks te bewaken. Vanuit de lucht wordt het mogelijk.'),
   T('Today: one drone system, two missions — border surveillance on the {river}, and search & rescue in dense forest.', 'Vandaag: één dronesysteem, twee missies — grensbewaking op de {river}, en zoeken en redden in dicht bos.'),
   T('What you see is a simulation of a real flight plan, on the real geography of Suriname.', 'Wat u ziet is een simulatie van een echt vliegplan, op de echte geografie van Suriname.')],
  'notes': T(
   '• Introduce DAMbv: drone inspection & autonomy company, EASA-licensed pilots, own ground-control software (this screen).\n• We operate under EASA Specific-category documents (ConOps, Operations Manual, quality system).\n• Say clearly: this is a simulation on real maps — the flight paths, distances and timings are realistic.\n• Point at the red line: the {river} border sector we will patrol.',
   '• Stel DAMbv voor: drone-inspectie en autonome systemen, EASA-gelicentieerde piloten, eigen grondstation-software (dit scherm).\n• We werken volgens EASA Specific-categorie documenten (ConOps, Operations Manual, kwaliteitssysteem).\n• Zeg duidelijk: dit is een simulatie op echte kaarten — routes, afstanden en tijden zijn realistisch.\n• Wijs de rode lijn aan: het grensgedeelte aan de {river} dat we bewaken.')},
 {'id': 'launch', 'title': T('Launch from Paramaribo', 'Opstijgen vanuit Paramaribo'),
  'captions': [
   T('Pre-flight checks run automatically: GPS, datalink, battery, weather and geofence.', 'Vluchtvoorbereiding gaat automatisch: GPS, datalink, batterij, weer en geofence.'),
   T('Vertical take-off — no runway needed.', 'Verticale start — geen landingsbaan nodig.'),
   T('Hovering at 40 metres — all systems checked, ready to depart.', 'Zweven op 40 meter — alle systemen gecontroleerd, klaar voor vertrek.')],
  'notes': T(
   '• VTOL = takes off like a helicopter, flies like an aeroplane: long range without a runway.\n• Real operations need authorisation from CASAS (Civil Aviation Safety Authority Suriname). We prepare the risk assessment and procedures.\n• The geofence is loaded before take-off — the aircraft physically cannot be commanded across the border.',
   '• VTOL = start als een helikopter, vliegt als een vliegtuig: groot bereik zonder landingsbaan.\n• Echte operaties vereisen toestemming van CASAS (Civil Aviation Safety Authority Suriname). Wij stellen de risicoanalyse en procedures op.\n• De geofence wordt vóór de start geladen — het toestel kan niet over de grens gestuurd worden.')},
 {'id': 'transit', 'title': T('Transit to the {river} border', 'Transit naar de grens aan de {river}'),
  'captions': [
   T('Transition to efficient fixed-wing flight, then climb to cruise altitude.', 'Overgang naar zuinige vleugelvlucht, daarna klimmen naar kruishoogte.'),
   T('{transit_km} km from the capital to {fob} — about {transit_min} minutes of flight. Shown here faster.', '{transit_km} km van de hoofdstad naar {fob} — ongeveer {transit_min} minuten vliegen. Hier versneld getoond.'),
   T('The aircraft flies itself along an approved corridor. The operator supervises.', 'Het toestel vliegt zelfstandig langs een goedgekeurde corridor. De operator houdt toezicht.'),
   T('Landing at the forward base in {fob}. In daily operations the aircraft are stationed here.', 'Landing op de voorwaartse basis in {fob}. In de dagelijkse praktijk staan de toestellen hier gestationeerd.')],
  'notes': T(
   '• Honest point on range: radio links reach roughly 15–30 km. Over {transit_km} km we use 4G/LTE where available or a satellite/relay link. This is part of the design for Suriname.\n• Endurance shown is based on the aircraft setting in scenarios.json — confirm real numbers in Surinamese heat and humidity during a pilot.\n• Concept of operations: aircraft are based at forward bases ({fob}); the capital is the command centre.',
   '• Eerlijk over bereik: radioverbindingen halen ca. 15–30 km. Over {transit_km} km gebruiken we 4G/LTE waar beschikbaar of een satelliet-/relaisverbinding. Dat is onderdeel van het ontwerp voor Suriname.\n• De getoonde vliegduur volgt de toestelinstelling in scenarios.json — echte waarden bevestigen we tijdens een pilot in Surinaamse hitte en vochtigheid.\n• Werkwijze: toestellen staan op voorwaartse bases ({fob}); de hoofdstad is het commandocentrum.')},
 {'id': 'patrol', 'title': T('Border surveillance — {river}', 'Grensbewaking — {river}'),
  'captions': [
   T('Battery swapped — second flight from {fob}. Patrolling the {river}, always on the Surinamese side.', 'Batterij gewisseld — tweede vlucht vanuit {fob}. Patrouille langs de {river}, altijd aan Surinaamse zijde.'),
   T('Artificial intelligence scans the camera image continuously for boats, people and vehicles.', 'Kunstmatige intelligentie scant het camerabeeld continu op boten, personen en voertuigen.'),
   T('ALERT — unregistered river crossing. The drone holds position and keeps the boat in view.', 'MELDING — niet-geregistreerde rivierovertocht. De drone blijft in positie en houdt de boot in beeld.'),
   T('Position, time and image go instantly to Border Command and the nearest police unit.', 'Positie, tijd en beeld gaan direct naar het Grenscommando en de dichtstbijzijnde politie-eenheid.'),
   T('The police boat is guided to the landing point — contact handed over.', 'De politieboot wordt naar het landingspunt geleid — contact overgedragen.')],
  'notes': T(
   '• The drone never leaves Surinamese airspace: it observes from {standoff} m inside the border (orange geofence line).\n• Day and night: thermal camera sees people and engines in darkness.\n• Honest: the AI detection and camera image here are simulated. In a pilot we train our models on real {river} traffic.\n• Value: one aircraft watches tens of kilometres of river per flight; patrol boats go only where they are needed.',
   '• De drone verlaat nooit het Surinaamse luchtruim: hij observeert vanaf {standoff} m binnen de grens (oranje geofence-lijn).\n• Dag en nacht: de warmtecamera ziet mensen en motoren in het donker.\n• Eerlijk: de AI-detectie en het camerabeeld zijn hier gesimuleerd. In een pilot trainen we onze modellen op echt verkeer op de {river}.\n• Waarde: één toestel bewaakt tientallen kilometers rivier per vlucht; patrouilleboten gaan alleen waar ze nodig zijn.')},
 {'id': 'sar', 'title': T('Search & rescue — missing person', 'Zoek- en reddingsactie — vermiste persoon'),
  'captions': [
   T('New task: a hunter has been missing for 20 hours in the {sar_place}.', 'Nieuwe opdracht: een jager wordt al 20 uur vermist in het {sar_place}.'),
   T('Search area planned automatically around the last known position — lines every 120 metres.', 'Zoekgebied automatisch gepland rond de laatst bekende positie — elke 120 meter een zoeklijn.'),
   T('The thermal camera detects body heat through gaps in the canopy — day and night.', 'De warmtecamera ziet lichaamswarmte door openingen in het bladerdak — dag en nacht.'),
   T('POSSIBLE PERSON — heat signature detected. The drone breaks off the search to confirm.', 'MOGELIJK PERSOON — warmtebron gedetecteerd. De drone onderbreekt de zoektocht om te bevestigen.'),
   T('Confirmed. Exact coordinates and a walking route go to the rescue team.', 'Bevestigd. Exacte coördinaten en een looproute gaan naar het reddingsteam.')],
  'notes': T(
   '• Honest expectation: thermal cameras cannot see through closed canopy. They work best at night and early morning, through canopy gaps and along rivers and clearings. That is why we fly systematic lines and work together with ground teams.\n• Speed is the value: {area_km2} km² searched in minutes, where a ground team needs many hours.\n• Optional payload: drop of water, radio and first-aid kit (depends on aircraft configuration).',
   '• Eerlijke verwachting: warmtecamera’s kijken niet door een gesloten bladerdak. Ze werken het best ’s nachts en vroeg in de ochtend, door openingen in het bladerdak en langs rivieren en open plekken. Daarom vliegen we systematische lijnen en werken we samen met grondteams.\n• De waarde is snelheid: {area_km2} km² doorzocht in minuten, waar een grondteam vele uren nodig heeft.\n• Optionele lading: afwerpen van water, radio en EHBO-pakket (afhankelijk van toestelconfiguratie).')},
 {'id': 'return', 'title': T('Return and landing', 'Terugkeer en landing'),
  'captions': [
   T('Mission complete — automatic return to {fob}.', 'Missie voltooid — automatische terugkeer naar {fob}.'),
   T('Vertical landing. Ready for the next task within minutes.', 'Verticale landing. Binnen enkele minuten klaar voor de volgende opdracht.')],
  'notes': T(
   '• Everything was flown by the autopilot; the operator only approved decisions.\n• Same aircraft, same day: border patrol and search & rescue.',
   '• Alles is door de automatische piloot gevlogen; de operator keurde alleen beslissingen goed.\n• Hetzelfde toestel, dezelfde dag: grensbewaking én zoeken en redden.')},
 {'id': 'report', 'title': T('Mission report', 'Missierapport'), 'captions': [],
  'notes': T(
   '• Every alert has a position, a time and an image — usable as evidence and for planning.\n• Data stays on Surinamese systems (data sovereignty).',
   '• Elke melding heeft een positie, een tijd en een beeld — bruikbaar als bewijs en voor planning.\n• Data blijft op Surinaamse systemen (datasoevereiniteit).')},
 {'id': 'closing', 'title': T('What this means for Suriname', 'Wat dit betekent voor Suriname'), 'captions': [],
  'points': [
   T('Eyes on the border rivers — day and night', 'Ogen op de grensrivieren — dag en nacht'),
   T('Missing persons found in hours instead of days', 'Vermiste personen gevonden in uren in plaats van dagen'),
   T('A fraction of the cost of helicopter flight hours', 'Een fractie van de kosten van helikoptervlieguren'),
   T('Data stays in Suriname — your system, your control', 'Data blijft in Suriname — uw systeem, uw controle'),
   T('Local operators and technicians trained by DAMbv', 'Lokale operators en technici opgeleid door DAMbv'),
   T('Start: a 3-month pilot on one border sector', 'Start: een pilot van 3 maanden op één grenssector')],
  'notes': T(
   '• Ask for the next step: a pilot on one border sector, with Surinamese operators in the team from day one.\n• Offer: a joint workshop with Border Command, Police and the Fire Service/SAR to define the first missions.',
   '• Vraag om de volgende stap: een pilot op één grenssector, met Surinaamse operators vanaf dag één in het team.\n• Aanbod: een gezamenlijke workshop met Grenscommando, Politie en Brandweer/SAR om de eerste missies te bepalen.')}
]

scen = {
 'albina': {'title': T('Eastern border — Albina, Marowijne river', 'Oostgrens — Albina, Marowijnerivier'),
   'names': {'river': T('Marowijne', 'Marowijne'), 'fob': T('Albina', 'Albina'),
             'sar_place': T('forest south of the Albina–Moengo road', 'bos ten zuiden van de weg Albina–Moengo'),
             'neighbour': T('French Guiana', 'Frans-Guyana')},
   'contact': {'title': T('Unregistered river crossing', 'Niet-geregistreerde rivierovertocht'),
               'detail': T('Boat · 2 persons · heading to Surinamese bank', 'Boot · 2 personen · koers naar Surinaamse oever'),
               'recipients': T('Border Command · Police boat Albina', 'Grenscommando · Politieboot Albina'),
               'unit': T('Police boat Albina', 'Politieboot Albina'), 'unit_kmh': 35},
   'sar_info': {'team': T('Search team from the Albina–Moengo road', 'Zoekteam vanaf de weg Albina–Moengo'), 'team_kmh': 3},
   'standoff_m': 650},
 'nickerie': {'title': T('Western border — Nickerie, Corantijn river', 'Westgrens — Nickerie, Corantijnrivier'),
   'names': {'river': T('Corantijn', 'Corantijn'), 'fob': T('Nieuw Nickerie', 'Nieuw Nickerie'),
             'sar_place': T('forest east of the Corantijn', 'bos ten oosten van de Corantijn'),
             'neighbour': T('Guyana', 'Guyana')},
   'contact': {'title': T('Unregistered river crossing', 'Niet-geregistreerde rivierovertocht'),
               'detail': T('Boat · 2 persons · off the ferry route', 'Boot · 2 personen · buiten de veerroute'),
               'recipients': T('Border Command · Police boat South Drain', 'Grenscommando · Politieboot South Drain'),
               'unit': T('Police boat South Drain', 'Politieboot South Drain'), 'unit_kmh': 35},
   'sar_info': {'team': T('Rescue team by boat on the Corantijn', 'Reddingsteam per boot op de Corantijn'), 'team_kmh': 3},
   'standoff_m': 700}
}
for k, s in scen.items():
    s.update(G[k])
out = {'_readme': 'InfraDrone Suriname demo scenarios. Geometry computed from OpenStreetMap/geoBoundaries borders (ODbL); every route was checked to stay inside Suriname and at least 450 m from the land border. Edit texts freely; edit coordinates only with care.',
       **common, 'chapters': chapters, 'scenarios': scen}
json.dump(out, open('scenarios.json', 'w'), ensure_ascii=False, indent=1)
import os; print('scenarios.json', os.path.getsize('scenarios.json'), 'bytes')
