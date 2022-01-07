open System
open Google.Apis.Services
open System.IO
open Google.Apis.YouTube.v3
open System.Net.Http
open System.Text.RegularExpressions
open FSharp.Control
open Argu

let apiKey = File.ReadAllText("./google-api-key.txt")

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

module ImageDownloading = 
    let getFileExtension url = 
        let uri = url  |> Uri
        uri.GetLeftPart(UriPartial.Path) |> Path.GetExtension
    
    let createPath folder title url = 
        let fileName = Regex.Replace(title, @"[\\/:*?""<>|]", "")
        let fileExtension = getFileExtension url
    
        Path.Combine(folder, $"{fileName}{fileExtension}")
    
    let downloadImage data folder (client:HttpClient) = 
        async {
            let path = createPath folder data.Title data.Url
    
            let! imageBytes = client.GetByteArrayAsync(data.Url) |> Async.AwaitTask
            do! File.WriteAllBytesAsync(path, imageBytes) |> Async.AwaitTask
            return (data, path)
        }
    
    let downloadAllImages (images: AsyncSeq<VideoData>) folderPath = 
        Directory.CreateDirectory(folderPath) |> ignore
        let client = new HttpClient()
    
        //let! a = images |> AsyncSeq.mapAsyncParallel (fun x -> return x)
        images |> AsyncSeq.mapAsyncParallel (fun x -> downloadImage x folderPath client)

        //let! a = images |> AsyncSeq.mapAsyncParallel (fun x -> downloadImage x.Title x.Url folderPath client )
        //do! images |> AsyncSeq.toObservable |> Observable.bind Observable.toAs |> Observable.iter (fun x -> printfn "%A" x; downloadImage x.Title x.Url folderPath client)
        //do! images |> AsyncSeq.iterAsyncParallel (fun x -> printfn "%A" x; downloadImage x.Title x.Url folderPath client)
        

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

    yield! getAllVideoPages service playlistId
}

module StackCounterMailbox= 
    type StackProcessorMessage<'T> = Send of 'T | Get of AsyncReplyChannel<'T option * int>

    // Holds last value it recieved and maintains a count of how many values received
    let stackProcessor<'T> = 
        MailboxProcessor.Start(fun inbox ->
            let rec loop (latestState:'T option) counter = 
                async{
                    let! msg = inbox.Receive()
                    match msg with  
                    | Send newState -> 
                        return! loop (Some newState) (counter + 1)
                    | Get channel -> 
                        channel.Reply(latestState, counter) 
                        return! loop latestState counter
                }
            loop None 0
            )

open StackCounterMailbox
open System.Threading
open Spectre.Console

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CommandArguments>()
    let results = parser.Parse(inputs = argv, raiseOnUsage = true) 

    let channelName = results.GetResult Channel_Name
    let path = results.GetResult Save_Path

    let fromDate = match results.TryGetResult After_Date with
                   | Some date -> DateTime.Parse date
                   | None -> DateTime.Parse "12/1/2019 8:30:52 AM"

    let isAfterDate vid = match vid.Upload with
                          | Some value -> value > fromDate;
                          | _-> false

    let stopWatch = System.Diagnostics.Stopwatch.StartNew()

    let downloader images = ImageDownloading.downloadAllImages images path 

    let retrievedDataChannel = stackProcessor
    let downloadedChannel = stackProcessor

    let downloadTask = getChannelUploads channelName |> AsyncSeq.map playlistItemToVideoData 
                        |> AsyncSeq.takeWhile isAfterDate 
                        |> AsyncSeq.map (fun x-> retrievedDataChannel.Post(Send x) |> ignore; x)
                        |> downloader 
                        |> AsyncSeq.iter (fun x-> downloadedChannel.Post(Send x) |> ignore)


    AnsiConsole.WriteLine($"About to start downloading thumbnails from {channelName} to path {path}")
    
    let table = Table().HeavyBorder()
                    .AddColumn("[yellow]Process[/]") 
                    .AddColumn("[yellow]Current value[/]") 
                    .AddColumn("[yellow]Count[/]")
                    .AddRow("Retrieved:","","0")
                    .AddRow("Downloaded:","","0/0");


    
    let mutable a = AnsiConsole.Live(table)
    a.AutoClear <- true
    a.Overflow <- VerticalOverflow.Ellipsis
    a.Cropping <- VerticalOverflowCropping.Top
    let a = a.StartAsync((fun ctx -> 
        task{
            let startedDownloadTask = downloadTask |> Async.StartAsTask

            let timeSpan = TimeSpan.FromMilliseconds(100)
            let periodicTimer = new PeriodicTimer(timeSpan)

            while not (startedDownloadTask.IsCompleted) do 
                let retrievedData,retrievedCount = retrievedDataChannel.PostAndReply(fun rc -> Get rc)
                let downloadedData,dretrievedCount = downloadedChannel.PostAndReply(fun rc -> Get rc)

                do match retrievedData with 
                   | Some value -> table.UpdateCell(0,1,value.ToString()).UpdateCell(0,2,retrievedCount.ToString()) |> ignore
                   | _-> ()          
                                   
                do match downloadedData with 
                   | Some value -> table.UpdateCell(1,1,value.ToString())
                                        .UpdateCell(1,2,$"{dretrievedCount}/{retrievedCount}") |> ignore
                   | _-> ()     
                                       
                ctx.Refresh()
                do! periodicTimer.WaitForNextTickAsync().AsTask() |> Async.AwaitTask |> Async.Ignore
            
            ctx.Refresh()

        }))

    a |> Async.AwaitTask |> Async.RunSynchronously

   
    //printfn "%f" stopWatch.Elapsed.TotalMilliseconds

    0
