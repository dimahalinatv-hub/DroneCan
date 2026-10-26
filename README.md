# Hobbywing CAN Monitor

.NET MAUI (Windows) застосунок для читання конфігурації та телеметрії ESC **Hobbywing X8 G2**
(і інших Hobbywing DroneCAN ESC), підключених по CAN до польотника з **ArduPilot**.
Застосунок під'єднується до польотника по COM-порту (USB) і працює через MAVLink.

## Як це працює

```
ПК (COM/USB) ──MAVLink──► ArduPilot ──CAN──► Hobbywing ESC
```

1. Після першого `HEARTBEAT` застосунок щосекунди надсилає `MAV_CMD_CAN_FORWARD` (32000) —
   ArduPilot починає пересилати кадри вибраної CAN-шини у `CAN_FRAME` (той самий механізм, що в
   Mission Planner / DroneCAN GUI «MAVLink-CAN»). Опційно («Forward only ESC traffic», за замовчуванням
   вимкнено) `CAN_FILTER_MODIFY` обмежує пересилання лише типами, які застосунок декодує.
2. Кадри збираються в DroneCAN-трансфери (багатофреймові з перевіркою CRC) і декодуються.
3. Запити до ESC (`GetNodeInfo`, `GetMajorConfig`, …) відправляються назад як `CAN_FRAME` від
   імені вузла застосунку (за замовчуванням node ID 126).

### Що показується

**Жива телеметрія (з CAN):**

| Повідомлення | Дані |
|---|---|
| `com.hobbywing.esc.StatusMsg1` (20050) | оберти (RPM), PWM, статус (hex) |
| `com.hobbywing.esc.StatusMsg2` (20051) | напруга (0.1 В), струм (0.1 А), температура |
| `com.hobbywing.esc.StatusMsg3` (20052) | температури MOSFET / конденсатора / мотора |
| `com.hobbywing.esc.GetEscID` (20013) | номер throttle-каналу кожного ESC |
| `uavcan.equipment.esc.Status` (1034) | якщо ESC шле стандартний статус |
| `uavcan.equipment.esc.StatusExtended` (1036) | температура мотора, вхід/вихід % |
| `uavcan.protocol.NodeStatus` (341) | health / mode |

Панель **Bus traffic** показує всі DroneCAN-трансфери, що надходять: від якого вузла, який тип
(з назвою з офіційного DSDL), частота та лічильник. Якщо ESC шле щось, чого таблиця ESC не
розбирає, це буде видно тут.

Також показується телеметрія самого ArduPilot (`ESC_TELEMETRY_1_TO_4`, `5_TO_8`) — для порівняння.

**Конфігурація (тільки читання):** зчитується автоматично одразу, щойно вибрано вузол у списку
«Node» (і після кожного запису), якщо апарат роззброєний; кнопка «↻» перечитує її вручну.

- `uavcan.protocol.GetNodeInfo` — назва, версії ПЗ/заліза, unique ID;
- `com.hobbywing.esc.GetMajorConfig` (242) — напрям, джерело газу (CAN/PWM), throttle-канал,
  LED, частоти StatusMsg1/2, кут позиціонування;
- `com.hobbywing.esc.SetDirection` з `DIRECTION_QUERY = 255` — лише запит, нічого не змінює;
- `com.hobbywing.esc.SetReportingFrequency` з `OPTION_READ` — частоти StatusMsg1/2/3;
- `com.hobbywing.esc.GetMaintenanceInformation` (241) з `option = 0` — напрацювання (лічильник **не** скидається);
- `uavcan.protocol.param.GetSet` — список стандартних параметрів, якщо ESC їх підтримує.

Для кожної відповіді в звіті є сирий payload у hex (група «Raw») — щоб можна було звірити розбір
з документацією Hobbywing, якщо якесь поле виглядає дивно. «Copy report» копіює звіт у буфер обміну.

> Порівняно з DroneCAN GUI: його Hobbywing-панель з `GetMajorConfig` показує лише throttle source,
> а для Msg2/Msg3 rate бере значення з поля Msg1. Тут розбираються всі поля відповіді.

У таблиці ESC:

- **Thr src** — джерело газу (CAN/PWM). Деякі прошивки X8 G2 не відповідають на `GetMajorConfig`, а іншого
  запиту лише для читання для throttle source і LED немає. Тоді ці значення невідомі (`–`, «unknown» у звіті)
  і з'являються лише після «Set»: ESC у відповіді повертає застосоване значення;
- **Dir** — напрям, налаштований в ESC (відповідь на `SetDirection` з запитом 255, інакше з
  `GetMajorConfig`). Біт 14 статусу StatusMsg1 (його використовує DroneCAN GUI) на X8 G2 напряму
  не відповідає, тому не використовується. Фактичне обертання залежить ще й від того, як
  під'єднані фази мотора: якщо поміняти два дроти, при налаштуванні CW мотор крутиться CCW;
- **Msg1 set / Msg2 set / Msg3 set** — частоти StatusMsg1/2/3, налаштовані в ESC;
- **Measured, Hz** — частоти, з якими повідомлення реально приходять.

Ці налаштування застосунок читає автоматично для кожного Hobbywing ESC (тільки поки апарат роззброєний).

## Зміна налаштувань (кнопка «Edit», назад — «Report»)

| Налаштування | Сервіс | Примітка |
|---|---|---|
| CAN speed | `SetBaud` (211) | див. нижче: помилкова швидкість обриває зв'язок з ESC |
| Node ID | `SetID` (210) | throttle-канал лишається тим самим; ESC з'явиться як новий вузол |
| Throttle channel | `SetID` (210) | node ID лишається тим самим |
| Throttle source CAN / PWM | `SetThrottleSource` (215) | у режимі PWM ESC **ігнорує газ з ArduPilot по CAN** |
| StatusMsg1 / 2 / 3 rate | `SetReportingFrequency` (214), `OPTION_WRITE` | 500…1 Гц або Off; Msg1 (RPM) використовується ArduPilot, зокрема для RPM-notch |
| LED color / mode | `SetLED` (212) | «Save» — зберегти в ESC |
| Direction CW / CCW | `SetDirection` (213) | |

### ⚠ X8 G2: SetID діє на всі ESC на шині

Прошивка Hobbywing X8 G2 виконує `SetID` (node ID + throttle channel) на **кожному** ESC, що
підключений до CAN, незалежно від того, якому вузлу адресовано запит (з заводу всі ESC мають ID 1,
і налаштовувати їх треба по одному). Тому застосунок:

- перед зміною node ID / throttle channel **попереджає**, якщо в таблиці видно інші Hobbywing ESC
  (дані за останні 3 с) або вузол схожий на кілька ESC з однаковим ID, і питає, чи продовжувати;
- перед **будь-яким** записом надсилає нешкідливий `GetMajorConfig`; якщо відповів ще хтось, крім
  цільового ESC, у вікні підтвердження перелічено, які ESC теж буде змінено.
  Запис на кілька ESC одночасно дозволено, але тільки свідомо: «Write» — записати, «Cancel» — ні;
- після запису попереджає, якщо його несподівано застосували й інші ESC (вони теж надсилають відповідь);
- у колонці Health показує `⚠ shared node ID?` (кілька ESC з одним ID) або `⚠ answers for others`.

**Як виправити ESC, яким уже записали однаковий ID / канал:**

1. Від'єднайте CAN (або живлення) всіх ESC, крім одного.
2. Зачекайте, поки інші зникнуть з таблиці; в Edit виставте цьому ESC його throttle channel
   (= номер виходу мотора в ArduPilot) і унікальний node ID.
3. Від'єднайте його, підключіть наступний ESC і повторіть. Наприкінці підключіть усі.

Кожен запис потребує підтвердження в діалозі з поясненням наслідків. Після запису застосунок
перевіряє, що ESC повернув саме записане значення, і перечитує конфігурацію. Поточні значення
з ESC підставляються в поля автоматично при виборі вузла.

**CAN speed** (`SetBaud`, 1 Mbit/s … 50 kbit/s) прочитати неможливо, ESC відповідає порожньою відповіддю.
Після зміни ESC працює лише на новій швидкості: якщо `CAN_Px_BITRATE` польотного контролера інший,
ESC зникає з шини (ні газу, ні телеметрії), і повернути швидкість через застосунок уже не вийде.

### Пресети (кнопка «Presets…»)

Набір налаштувань можна зберегти під назвою і потім записати в будь-який ESC одним діянням.

- **Save**: береться те, що зараз стоїть у полях редактора, лише позначені галочками поля зі значенням.
  За замовчуванням позначено все, крім CAN speed, Node ID і Throttle channel — вони або свої в кожного ESC,
  або можуть відрізати ESC від шини. Та сама назва — перезапис (після підтвердження).
- **Apply to ESC**: значення пресету підставляються в поля редактора, далі **одне** підтвердження з переліком
  усіх записів і попереджень, і записи йдуть по черзі: throttle source → StatusMsg1/2/3 → LED → direction →
  `SetID` (node ID і throttle channel одним запитом) → CAN speed останнім. Поля, які ESC уже повідомив з тим
  самим значенням, не записуються; на першому невдалому записі решта не виконується. Після зміни node ID
  CAN speed пишеться вже на новий ID.
- Пресети зберігаються у `presets.json` у теці даних застосунку (`FileSystem.AppDataDirectory`).

## Безпека

- Газ застосунок не надсилає ніколи. Налаштування ESC змінюються тільки кнопками «Set» або «Apply to ESC» пресету після підтвердження.
- Поки апарат **ARMED**, усі запити (GetEscID, GetNodeInfo, читання й запис конфігурації) блокуються —
  ArduPilot зазначає, що GetEscID на озброєному апараті може викликати «заїкання» деяких Hobbywing ESC.

## Налаштування ArduPilot

- `CAN_P1_DRIVER = 1` (або P2 для другого порту), `CAN_D1_PROTOCOL = 1` (DroneCAN).
- Для власної телеметрії ArduPilot з Hobbywing: `CAN_D1_UC_OPTION` біт 7 (HobbyWingESC).
- Прошивка ArduPilot 4.3+ (підтримка `MAV_CMD_CAN_FORWARD`).
- У застосунку «FC CAN bus» = номер фізичного CAN-порту (1 = CAN_P1).
- «App node ID» має бути вільним на шині (не збігатися з ArduPilot `CAN_D1_UC_NODE`, типово 10, та з ESC).
- Рекомендовано USB-підключення: пересилання CAN по телеметричному радіо 57600 не вистачить.

## Симулятор

У списку портів є `SIMULATOR` — вбудований імітатор ArduPilot з чотирма Hobbywing ESC
(телеметрія + відповіді на всі запити конфігурації). Запуск одразу з симулятором:

```bash
dotnet run --project src/HobbywingCan.App -- --simulate
```

ESC у симуляторі зберігають налаштування, тож запис можна перевірити без заліза: наприклад,
після зміни частоти StatusMsg1 змінюється реальна частота його повідомлень. `--settings` відкриває
панель налаштувань одразу при старті.

## Збірка та запуск

Потрібно .NET 10 SDK з workload `maui-windows`.

```bash
dotnet build src/HobbywingCan.App -c Release
```

```bash
dotnet run --project src/HobbywingCan.App
```

Тести (серіалізація DSDL звірена з pydronecan, MAVLink — з pymavlink, плюс наскрізний тест із
симульованим ArduPilot + ESC):

```bash
dotnet test tests/HobbywingCan.Core.Tests
```

## Структура

- `src/HobbywingCan.Core` — протоколи без UI: MAVLink парсер/енкодер, DroneCAN (бітова
  серіалізація як у libcanard, збирання трансферів, CRC, сервіс-запити), повідомлення Hobbywing,
  `MavlinkCanLink` (COM ↔ CAN), `EscMonitor`, `EscConfigReader`.
- `src/HobbywingCan.App` — MAUI UI (Windows).
- `tests/HobbywingCan.Core.Tests` — xUnit.
