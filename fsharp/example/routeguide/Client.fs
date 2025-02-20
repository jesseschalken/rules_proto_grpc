module Client

open Grpc.Core
open System
open System.Linq
open System.Text
open FSharp.Control


type Client(client: RouteGuide.RouteGuide.RouteGuideClient) =

    member this.GetFeature (lat: int) (lon: int) =
        try
            printfn "*** GetFeature: lat=%i lon=%i" lat lon

            let request : RouteGuide.Point =
                { Latitude = ValueSome(lat)
                  Longitude = ValueSome(lon)
                  _UnknownFields = null }

            let feature : RouteGuide.Feature = client.GetFeature(request)

            match (feature.Name, feature.Location) with
            | (ValueSome (name), ValueSome (location)) ->
                match (location.Latitude, location.Longitude) with
                | (ValueSome (lat), ValueSome (lon)) -> printfn "Found feature called \"%s\" at %i, %i" name lat lon
                | _ -> printfn "Point did not have coordinates"
            | _ -> printfn "Found no feature at %i, %i" lat lon
        with :? RpcException as ex -> printfn "Rpc failed: %s" ex.Message

    member this.ListFeatures (lowLat: int) (lowLon: int) (hiLat: int) (hiLon: int) =
        async {
            try
                printfn "*** ListFeatures:  lowLat=%i lowLon=%i hiLat=%i hiLon=%i" lowLat lowLon hiLat hiLon

                let request : RouteGuide.Rectangle =
                    { Lo =
                          ValueSome(
                              { Latitude = ValueSome(lowLat)
                                Longitude = ValueSome(lowLon)
                                _UnknownFields = null }
                          )
                      Hi =
                          ValueSome(
                              { Latitude = ValueSome(hiLat)
                                Longitude = ValueSome(hiLon)
                                _UnknownFields = null }
                          )
                      _UnknownFields = null }

                use call = client.ListFeaturesAsync(request)
                let responseLog = StringBuilder("Result: ")
                let mutable hasNext = true

                do!
                    call.ResponseStream.MoveNext()
                    |> Async.AwaitTask
                    |> Async.Ignore

                while hasNext do
                    let current = call.ResponseStream.Current
                    responseLog.Append(current.ToString()) |> ignore

                    let! next = call.ResponseStream.MoveNext() |> Async.AwaitTask
                    hasNext <- next

                printf "%s" (responseLog.ToString())
            with :? RpcException as ex -> printfn "Rpc failed: %s" ex.Message
        }
        |> Async.StartAsTask

    member this.RecordRoute (features: RouteGuide.Feature seq) (numPoints: int) =
        async {
            try
                printfn "%s" "*** RecordRoute"
                use call = client.RecordRouteAsync()
                let rand = Random()

                let requests : Async<unit> seq =
                    seq { 0 .. numPoints }
                    |> Seq.map
                        (fun i ->
                            let index = rand.Next(features.Count())
                            let point = (features |> Seq.item index).Location

                            match point with
                            | ValueSome (p) ->
                                match (p.Latitude, p.Longitude) with
                                | (ValueSome (lat), ValueSome (lon)) ->
                                    Some(
                                        async {
                                            printfn "Visiting point %i, %i" lat lon

                                            call.RequestStream.WriteAsync(p)
                                            |> Async.AwaitTask
                                            |> ignore

                                            do! Async.Sleep 1000
                                        }
                                    )
                                | _ ->
                                    printfn "%s" "Point did not have latitude and longitude"
                                    None
                            | _ ->
                                printfn "%s" "Feature did not have a point"
                                None)
                    |> Seq.choose id

                do! requests |> Async.Sequential |> Async.Ignore

                do!
                    call.RequestStream.CompleteAsync()
                    |> Async.AwaitTask

                let! summary = call.ResponseAsync |> Async.AwaitTask

                match (summary.PointCount, summary.FeatureCount, summary.Distance, summary.ElapsedTime) with
                | (ValueSome (p), ValueSome (f), ValueSome (d), ValueSome (e)) ->
                    printfn
                        "Finished trip with %i points. Passed %i features. Travelled %i meters. It took %i seconds."
                        p
                        f
                        d
                        e
                | _ -> printfn "%s" "Summary did not include full details"

                printfn "%s" "Finished RecordRoute"

            with :? RpcException as ex -> printfn "Rpc failed: %s" ex.Message
        }
        |> Async.StartAsTask

    member this.RouteChat() =
        async {
            try
                printfn "%s" "*** RouteChat"

                let requests : RouteGuide.RouteNote list =
                    [ { Message = ValueSome("First message")
                        Location =
                            ValueSome(
                                { Longitude = ValueSome(0)
                                  Latitude = ValueSome(0)
                                  _UnknownFields = null }
                            )
                        _UnknownFields = null }
                      { Message = ValueSome("Second message")
                        Location =
                            ValueSome(
                                { Longitude = ValueSome(0)
                                  Latitude = ValueSome(1)
                                  _UnknownFields = null }
                            )
                        _UnknownFields = null }
                      { Message = ValueSome("Third message")
                        Location =
                            ValueSome(
                                { Longitude = ValueSome(1)
                                  Latitude = ValueSome(0)
                                  _UnknownFields = null }
                            )
                        _UnknownFields = null }
                      { Message = ValueSome("Fourth message")
                        Location =
                            ValueSome(
                                { Longitude = ValueSome(0)
                                  Latitude = ValueSome(0)
                                  _UnknownFields = null }
                            )
                        _UnknownFields = null } ]

                use call = client.RouteChatAsync()

                let responseReaderTask =
                    async {
                        let mutable hasNext = true

                        do!
                            call.ResponseStream.MoveNext()
                            |> Async.AwaitTask
                            |> Async.Ignore

                        while hasNext do
                            let note = call.ResponseStream.Current
                            printfn "Got message \"%A\" at %A" note.Message note.Location

                            let! next = call.ResponseStream.MoveNext() |> Async.AwaitTask
                            hasNext <- next
                    }

                let requestsAsync =
                    requests
                    |> Seq.map
                        (fun r ->
                            printfn "Sending message \"%A\" at %A" r.Message r.Location

                            call.RequestStream.WriteAsync(r)
                            |> Async.AwaitTask)

                do! requestsAsync |> Async.Sequential |> Async.Ignore

                do!
                    call.RequestStream.CompleteAsync()
                    |> Async.AwaitTask

                do! responseReaderTask

                printfn "%s" "Finished RouteChat"
            with :? RpcException as ex -> printfn "Rpc failed: %s" ex.Message
        }
        |> Async.StartAsTask


[<EntryPoint>]
let main argv =
    Console.WriteLine("Starting Client...")

    let port =
        let portVar =
            System.Environment.GetEnvironmentVariable("SERVER_PORT")

        if not (String.IsNullOrEmpty(portVar)) then
            Int32.Parse(portVar)
        else
            50051

    let channel =
        Channel("127.0.0.1:" + port.ToString(), ChannelCredentials.Insecure)

    let routeGuideClient =
        RouteGuide.RouteGuide.RouteGuideClient(channel)

    let client = Client(routeGuideClient)

    // Looking for a valid feature
    client.GetFeature 409146138 -746188906

    // Feature missing.
    client.GetFeature 0 0

    // Looking for features between 40, -75 and 42, -73.
    (client.ListFeatures 400000000 -750000000 420000000 -730000000)
        .Wait()

    // Record a few randomly selected points from the features file.
    (client.RecordRoute(RouteGuideUtil.parseFeatures "fsharp/example/routeguide/client.exe/routeguide_features.json") 10)
        .Wait()

    // Send and receive some notes.
    (client.RouteChat()).Wait()

    channel.ShutdownAsync().Wait()
    0
