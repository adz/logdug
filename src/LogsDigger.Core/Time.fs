namespace LogsDigger

open System
open System.Globalization

type ZoneOption =
    { Id: string
      Label: string
      Zone: TimeZoneInfo }

/// The zone that timestamps are currently converted into, resolved once per change rather than per row.
type TimeContext =
    { Display: TimeDisplay
      Zone: TimeZoneInfo
      Caption: string }

module Time =
    let private ianaId (zone: TimeZoneInfo) =
        if zone.HasIanaId then
            zone.Id
        else
            match TimeZoneInfo.TryConvertWindowsIdToIanaId zone.Id with
            | true, iana -> iana
            | false, _ -> zone.Id

    let offsetText (offset: TimeSpan) =
        let sign = if offset < TimeSpan.Zero then "-" else "+"
        let magnitude = offset.Duration()
        $"UTC{sign}{magnitude.Hours:D2}:{magnitude.Minutes:D2}"

    let private option (now: DateTimeOffset) (zone: TimeZoneInfo) =
        let id = ianaId zone

        { Id = id
          Label = $"{id}  ({offsetText (zone.GetUtcOffset now)})"
          Zone = zone }

    /// Every zone the OS knows, labelled by IANA id with its offset at `now`, sorted by offset then name.
    let zones (now: DateTimeOffset) =
        TimeZoneInfo.GetSystemTimeZones()
        |> Seq.map (option now)
        |> Seq.distinctBy _.Id
        |> Seq.sortBy (fun zone -> zone.Zone.GetUtcOffset now, zone.Id)
        |> List.ofSeq

    let tryFindZone (id: string) =
        try
            Some(TimeZoneInfo.FindSystemTimeZoneById id)
        with _ ->
            None

    let context (local: TimeZoneInfo) (now: DateTimeOffset) (display: TimeDisplay) : TimeContext =
        let zone, name =
            match display with
            | Utc -> TimeZoneInfo.Utc, "UTC"
            | Local -> local, $"Local · {ianaId local}"
            | Zone id ->
                match tryFindZone id with
                | Some zone -> zone, ianaId zone
                | None -> TimeZoneInfo.Utc, $"{id} (unknown, showing UTC)"

        { Display = display
          Zone = zone
          Caption = $"{name}  {offsetText (zone.GetUtcOffset now)}" }

    let format (context: TimeContext) (timestamp: DateTimeOffset) =
        TimeZoneInfo
            .ConvertTime(timestamp, context.Zone)
            .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)

    let utcContext =
        { Display = Utc
          Zone = TimeZoneInfo.Utc
          Caption = "UTC  UTC+00:00" }
