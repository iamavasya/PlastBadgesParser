# PlastBadgesParser ![GitHub Release](https://img.shields.io/github/v/release/iamavasya/PlastBadgesParser)


CLI-утиліта для парсингу бібліотеки вмілостей Пласту, нормалізації даних і формування `badges.json` для подальшого використання в застосунках/контент-процесах.

Цей проєкт створений, як милиця і надбудова над збіркою всіх пластових вмілостей. Parser допоможе підготувати майбутню бібліотеку проб та вмілостей для проєкту ProjectK.

Мета: Зібрати всі вмілості в зручному dev-форматі, з яким розробники зможуть взаємодіяти і додавати до своїх додатків. Додатковою метою є підняти веб-апі для пластових вмілостей, аби ще більше спростити роботу розробникам-волонтерам.

Так як в процесі дослідження сайтів з вмілостями в мережі не було знайдено відкритої бібліотеки чи апі - було вирішено створити свою бібліотеку/підняти веб-апі. Цей інструмент, парсер, це перший крок до вирішення цієї проблеми і дозволяє зібрати вмілості в json формат. 

## Що робить інструмент

- Парсить вмілості з `https://plast.global/biblioteka-vmilostej/`.
- Завантажує векторні бейджі у папку `badges_images`.
- Формує `badges.json` з метаданими (`Meta`) і масивом `Badges`.
- Підтримує soft-fixer для нормалізації даних.
- Підтримує режим `report-only` для аналізу проблемних записів без перезапису даних.

## Технології

- .NET `net10.0`
- `HtmlAgilityPack`
- `ReverseMarkdown`

## Структура даних

Вихідний JSON має формат:

- `Meta`
	- `ParserVersion`
	- `ToolAuthor`
	- `ParserComment`
	- `ParsedAtUtc`
	- `SourceUrl`
	- `FixerEnabled`
	- `FixerMode`
	- `TotalBadges`
- `Badges[]`
	- основні поля вмілості (`Id`, `Title`, `ImagePath`, ...)
	- `FixNotes` (примітки для ручного допрацювання)

## Локальний запуск

### 1) Повний парсинг

```bash
dotnet run --project ./PlastBadgesParser.csproj
```

#### 1.1) Спрощений запуск (в корені репо)
```bash
dotnet run
```

### 2) Вимкнути fixer

```bash
dotnet run --project ./PlastBadgesParser.csproj -- --fixer=off
```

### 3) Report-only (без перезапису badges.json)

```bash
dotnet run --project ./PlastBadgesParser.csproj -- --report-only --input=badges.json
```

### 4) Зберегти report у файл

```bash
dotnet run --project ./PlastBadgesParser.csproj -- --report-only --input=badges.json --report-out=fix-report.txt
```

## CLI параметри

- `--report-only` - запустити лише аналіз/репорт.
- `--input=<path>` - шлях до вхідного JSON для report-only.
- `--report-out=<path>` - зберегти репорт у файл.
- `--link-base=<url>` - базовий URL для посилань на сторінки вмілостей у report-only.
- `--fixer=off` або `--no-fixer` - вимкнути fixer.
- `--fixer=on` - увімкнути fixer.
- `--fixer-mode=soft` - явно встановити soft режим.
- `--project-file=<path>` - явний шлях до `.csproj` для визначення версії парсера.

## Реліз через GitHub Actions (по тегу)

Workflow: `.github/workflows/release-by-tag.yml`

Тригер:

- push тега формату `v*.*.*` (наприклад `v1.2.3`)

Що робить CI:

1. Бере версію з тега (`v1.2.3` -> `1.2.3`) і прокидує в `PARSER_VERSION`.
2. Збирає parser binary з цією версією.
3. Формує data snapshot із репозиторію (`badges.json` + `badges_images`).
4. Патчить у snapshot `badges.json` поле `Meta.ParserVersion` на версію з тега.
5. Створює GitHub Release та додає assets:
	 - `plast-badges-parser-<version>.zip`
	 - `plast-badges-data-<version>.zip`

Це дозволяє:

- зберегти ручні правки даних, якщо вони закомічені в тегнутому коміті;
- отримати консистентну версію у release-asset навіть якщо у репо було старе значення версії у `badges.json`.

## Типовий релізний сценарій

```bash
git add .
git commit -m "Prepare release data"
git tag v1.2.3
git push origin main
git push origin v1.2.3
```

## Нотатки

- CI бачить лише закомічені зміни.
- Якщо `badges.json` або `badges_images` відсутні у репо, workflow впаде на валідації.

<!-- TODO(Дорожня карта): Додай 3-5 наступних кроків, які хочеш реалізувати в проєкті. -->
