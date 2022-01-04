open System
open Google.Apis.Services
open System.IO
open Google.Apis.YouTube.v3
open System.Net.Http
open System.Text.RegularExpressions
open FSharp.Control
open Argu

let apiKey = File.ReadAllText("./google-api-key.txt")
let saveLocation = @"C:\Users\timma\Desktop\ImageData\ColorThumbnails\";

type CommandArguments = 
        |[<Mandatory>] Save_Path of path:string
        |[<Mandatory>] Channel_Name of channel:string
        | After_Date of from_date:string 

        interface IArgParserTemplate with
              member s.Usage =
                  match s with
                  | Save_Path _ -> "specify the save location."
                  | Channel_Name _ -> "specify the youtube channel to scrape."
                  | After_Date _ -> "specify the date from which uploads will be collected."

type VideoData = {Title:string; Url:string; Upload: DateTime option}

let getFileExtension url = 
    let uri = url  |> Uri
    uri.GetLeftPart(UriPartial.Path) |> Path.GetExtension

let createPath folder title url = 
    let fileName = Regex.Replace(title, @"[\\/:*?""<>|]", "")
    let fileExtension = getFileExtension url

    Path.Combine(folder, $"{fileName}{fileExtension}")

let downloadImage title url folder (client:HttpClient) = 
    async {
        let path = createPath folder title url

        let! imageBytes = client.GetByteArrayAsync(url) |> Async.AwaitTask
        return! File.WriteAllBytesAsync(path, imageBytes) |> Async.AwaitTask
    }

let playlistItemToVideoData (item:Data.PlaylistItem ) =
    let nullUpload = item.Snippet.PublishedAt
    let upload = if nullUpload.HasValue then Some  nullUpload.Value else None
    {Title=item.Snippet.Title; Url=item.Snippet.Thumbnails.Default__.Url; Upload= upload}

let rec getVideoPage (service:YouTubeService) playlistId pageToken = 
    async {
        if pageToken = null then   
            return None
        else
            let mutable request = service.PlaylistItems.List("snippet")
            
            request.PlaylistId <- playlistId
            request.PageToken <- pageToken
            request.MaxResults <- 50L

            let! response = request.ExecuteAsync() |> Async.AwaitTask

            printfn "Retrieved %d items from id: %s with token %s" response.Items.Count playlistId pageToken

            let pageData = response.Items :> seq<_> 
            let nextToken= response.NextPageToken

            return Some (pageData, nextToken)
            }

let getAllVideoPages service playlistId = 
    let rec loop token = asyncSeq {
        let! result = getVideoPage service playlistId token 

        match result with 
        | Some (pages, nextToken) ->
            for page in pages do
                yield page

            yield! loop nextToken
        | _ -> ()
    }

    loop ""

let downloadAllImages (images: AsyncSeq<VideoData>) folderPath = 
    async {
        let client = new HttpClient()
        Directory.CreateDirectory(folderPath) |> ignore

        do! images |> AsyncSeq.iterAsyncParallel (fun x -> printfn "%A" x; downloadImage x.Title x.Url folderPath client)
    }

let getChannelUploadsPlaylistId (service:YouTubeService) channelName = async{
    let mutable channelsListRequest:ChannelsResource.ListRequest = service.Channels.List("contentDetails")
    channelsListRequest.ForUsername <- channelName;

    let! response =  channelsListRequest.ExecuteAsync() |> Async.AwaitTask

    let channel = response.Items :> seq<_> |> Seq.head
    let playlistId = channel.ContentDetails.RelatedPlaylists.Uploads
    return playlistId
}

let getChannelUploads channelName = asyncSeq {
    //Create service and get channel id
    let baseClient = new BaseClientService.Initializer(ApiKey = apiKey, ApplicationName = "ThumbnailDownloader") 
    let service = new YouTubeService(baseClient)

    let! playlistId = getChannelUploadsPlaylistId service channelName
    printfn "Retrieved id: %s for channel: %s" playlistId channelName

    //let! pages = getAllVideoPages service playlistId |> AsyncSeq.take 60 |> AsyncSeq.map playlistItemToVideoData |> AsyncSeq.iter (printfn "%A") 
    //return pages

    yield! getAllVideoPages service playlistId
}

let predi vid = 
    match vid.Upload with
    | Some value -> value > DateTime.Parse "12/1/2019 8:30:52 AM";
    | _-> false

let randomwait time=
       async{
           Console.WriteLine( $"started waiting :{time}")
           do! Async.Sleep(time*1000);
           Console.WriteLine( $"Waited:{time}")
           return time
       }

let stream=
    asyncSeq { 

        for waitTime = 10 to 13 do
            yield randomwait waitTime
        for waitTime = 5 downto 3 do
            yield randomwait waitTime
        Console.WriteLine( $"Now printing second values")
    }
// Increment or decrement by one.
type CounterMessage =
    | Increment
    | Decrement

let createProcessor initialState =
    MailboxProcessor<CounterMessage>.Start(fun inbox ->
        // You can represent the processor's internal mutable state
        // as an immutable parameter to the inner loop function
        let rec innerLoop state = async {
            printfn "Waiting for message, the current state is: %i" state
            let! message = inbox.Receive()
            // In each call you use the current state to produce a new
            // value, which will be passed to the next call, so that
            // next message sees only the new value as its local state
            match message with
            | Increment ->
                let state' = state + 1
                printfn "Counter incremented, the new state is: %i" state'
                return! innerLoop state'
            | Decrement ->
                let state' = state - 1
                printfn "Counter decremented, the new state is: %i" state'
                return! innerLoop state'
        }
        // We pass the initialState to the first call to innerLoop
        innerLoop initialState)

// Let's pick an initial value and create the processor
let processor = createProcessor 10

open FSharp.Control.Reactive
open System.Threading.Tasks
open System.Threading

let counter =
    MailboxProcessor.Start(fun inbox ->
        let rec loop n =
            async { do printfn "n = %d, waiting..." n
                    let! msg = inbox.Receive()
                    return! loop(n+msg) }
        loop 0);;

let makeCountingTask (counter:MailboxProcessor<int>) taskId  = async {
    let name = sprintf "Task%i" taskId
    for i in [1..3] do
        counter.Post(i)
    }

[<EntryPoint>]
let main argv =
    Console.WriteLine("Printing")
    let counter = counter
    counter.Post(5) 
    counter.Post(15)

    let messageExample5 =
        [1..5]
            |> List.map (fun i -> makeCountingTask counter i)
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

    Thread.Sleep(2000);

    //let task1 = stream |> AsyncSeq.mapAsyncParallel id // This runs all our randomWait tasks at once
    //            |> AsyncSeq.mapAsyncParallel randomwait
    //            |>AsyncSeq.iterAsyncParallel(fun time ->async{ Console.WriteLine($"printing for time : {time}")})
    //Async.RunSynchronously task1

    //Console.WriteLine("------Second point------")
    //let task = stream |> AsyncSeq.toObservable |> Observable.bind Observable.ofAsync |> Observable.map randomwait |> Observable.iter (fun time -> printfn "printing for time : %i" time)

    //Observable.wait task |> ignore

    //let parser = ArgumentParser.Create<CommandArguments>()
    //let results = parser.Parse(inputs = argv, raiseOnUsage = true) 
    //let usage = parser.PrintUsage() 

    //let channelName = results.GetResult Channel_Name
    //let path = results.GetResult Save_Path

    //printfn "%s" usage
    //printfn "%s" path
    //printfn "%s" channelName


    //let stopWatch = System.Diagnostics.Stopwatch.StartNew()

    //    // Get id
    //    // Get iterator for each video
    //    // Takewhile true
    //    // Download to path return data

    //let downloader images = downloadAllImages images path 
    
    //let a = getChannelUploads channelName |> AsyncSeq.map playlistItemToVideoData 
    //        |> AsyncSeq.takeWhile predi 
    //        |> downloader 

    //a |> Async.RunSynchronously
    //        //|> AsyncSeq.
    //        //|> downloadAllImages

    //printfn "%f" stopWatch.Elapsed.TotalMilliseconds

    0
