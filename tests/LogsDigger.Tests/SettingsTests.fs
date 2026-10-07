module LogsDigger.Tests.SettingsTests

open Xunit
open LogsDigger

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
