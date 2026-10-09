module LogDug.Tests.SettingsTests

open System
open Xunit
open LogDug

[<Fact>]
let ``settings round-trip through the schema codec`` () =
    let settings =
        { Settings.defaults with
            TimeMode = "zone"
            Zone = "Asia/Tokyo"
            Regex = true
            DarkTheme = false }

    Assert.Equal(Ok settings, Settings.parse (Settings.serialize settings))

[<Fact>]
let ``an unknown time mode is rejected with its path`` () =
    let json = """{"timeMode":"mars","zone":"UTC","regex":false,"matchCase":false,"darkTheme":true}"""

    match Settings.parse json with
    | Ok _ -> failwith "expected a schema error"
    | Error message -> Assert.Contains("timeMode", message)

[<Fact>]
let ``time display maps both ways`` () =
    for display in [ Utc; Local; Zone "Europe/Paris" ] do
        Assert.Equal(display, Settings.timeDisplay (Settings.withTimeDisplay display Settings.defaults))

[<Fact>]
let ``the target zone follows the OS zone until zone mode is chosen`` () =
    let local = TimeZoneInfo.CreateCustomTimeZone("Test/Adelaide", TimeSpan.FromHours 10.5, "Test", "Test")
    let defaults = Settings.defaults
    Assert.Equal("Test/Adelaide", (App.withLocalZoneDefault local defaults).Zone)

    // A zone the user picked stays, even when it is the placeholder's value.
    let chosen = Settings.withTimeDisplay (Zone defaults.Zone) defaults
    Assert.Equal(defaults.Zone, (App.withLocalZoneDefault local chosen).Zone)

    let other = { defaults with Zone = "Asia/Tokyo" }
    Assert.Equal("Asia/Tokyo", (App.withLocalZoneDefault local other).Zone)
