open System
open Google.Apis.Services
open System.IO
open Google.Apis.YouTube.v3
open System.Text.RegularExpressions
open FSharp.Control
open Argu
open Spectre.Console
open System.Threading.Tasks
open System.Net.Http
open System.Diagnostics

type DownloadersArgs = 
        |[<Mandatory>] Save_Path of path:string
        |[<Mandatory>] Channel_Name of channel:string
        |[<Mandatory>] Api_Key of api_key:string 
        | After_Date of from_date:string 
        | [<AltCommandLine("-d")>] DisplaySample

        interface IArgParserTemplate with
                member s.Usage =
                    match s with
                    | Save_Path _ -> "specify the save location."
                    | Channel_Name _ -> "specify the youtube channel to scrape."
                    | After_Date _ -> "specify the date from which uploads will be collected."
                    | Api_Key _ -> "specify the google api key."
                    | DisplaySample _ -> "print a selection of downloaded images."

let parseArgs arg = 
    let parser = ArgumentParser.Create<DownloadersArgs>()
    let results = parser.Parse(inputs = arg) 
        
    let channelName = results.GetResult Channel_Name
    let path = results.GetResult Save_Path
    let apiKey = results.GetResult Api_Key
        
    let printSamples = match results.TryGetResult DisplaySample with 
                        | Some DisplaySample -> true
                        | _ -> false
    
    let fromDate = results.GetResult(After_Date, defaultValue = "12/1/2000 9:00:00 AM") |> DateTime.Parse 
    (channelName, path, apiKey, fromDate, printSamples)

type VideoData = {Title:string; Url:string; Upload: DateTime option}

  
let downloadImage videoData folder (client:HttpClient) = 
    let getFileExtension url = 
        let uri = System.Uri(url)
        uri.GetLeftPart(UriPartial.Path) |> Path.GetExtension
    
    let createPath folder title url = 
        let fileName = Regex.Replace(title, @"[\\/:*?""<>|]", "")
        let fileExtension = getFileExtension url

        Path.Combine(folder, $"{fileName}{fileExtension}")

    async {
        let path = createPath folder videoData.Title videoData.Url
        let! imageBytes = client.GetByteArrayAsync(videoData.Url) |> Async.AwaitTask
        do! File.WriteAllBytesAsync(path, imageBytes) |> Async.AwaitTask
        return (videoData, path)
    }
    
let downloadAllImages (images: AsyncSeq<VideoData>) folderPath = 
    Directory.CreateDirectory(folderPath) |> ignore
    let client = new HttpClient()
    
    images |> AsyncSeq.mapAsyncParallel (fun videoData -> downloadImage videoData folderPath client)


let getVideoPage (service:YouTubeService) playlistId pageToken = 
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

let getIdAndService channelName apiKey = 
    let getChannelUploadsPlaylistId (service:YouTubeService) channelName = async{
        let mutable channelsListRequest:ChannelsResource.ListRequest = service.Channels.List("contentDetails")
        channelsListRequest.ForUsername <- channelName;
    
        let! response =  channelsListRequest.ExecuteAsync() |> Async.AwaitTask
    
        let channel = response.Items :> seq<_> |> Seq.head
        let playlistId = channel.ContentDetails.RelatedPlaylists.Uploads
        return playlistId
    }

    async {
        let baseClient = new BaseClientService.Initializer(ApiKey = apiKey, ApplicationName = "ThumbnailDownloader") 
        let service = new YouTubeService(baseClient)

        let! playlistId = getChannelUploadsPlaylistId service channelName 
        return (service, playlistId)
    }

type StackProcessorMessage<'T> = Send of 'T | Get of AsyncReplyChannel<'T option * int>

// Holds last value it recieved and maintains a count of how many values received
let stackProcessor<'T> = 
    MailboxProcessor.Start(fun inbox ->
        let rec loop (latestState:'T option) counter = 
            async{
                let! msg = inbox.Receive()
                match msg with  
                | Send newState -> return! loop (Some newState) (counter + 1)
                | Get channel -> 
                    channel.Reply(latestState, counter) 
                    return! loop latestState counter
            }
        loop None 0
        )

let printProgressAsync (downloadTask:Task) (retrievedDataChannel: MailboxProcessor<StackProcessorMessage<_>>) (downloadedChannel:MailboxProcessor<StackProcessorMessage<_>>) = 
    let createProgressTable = 
        let table = Table().HeavyBorder()
                        .AddColumn("[bold #f1fa8c]Process[/]") 
                        .AddColumn("[bold #f1fa8c]Title[/]") 
                        .AddColumn("[bold #f1fa8c]Uploaded[/]") 
                        .AddColumn("[bold #f1fa8c]Count[/]")
                        .AddRow("Most recently retrieved:","","","0")
                        .AddRow("Most recently downloaded:","","","0/0");
    
        table.Columns[1].Width <- 50
        table.Columns[2].Width <- 12
        table
    
    let table = createProgressTable

    let progressTask = AnsiConsole.Live(table).StartAsync((fun ctx -> 
        task{
            let timeSpan = TimeSpan.FromMilliseconds(100)

            let rec loop () = async{
                do! Task.Delay(timeSpan)|> Async.AwaitTask |> Async.Ignore

                let retrievedData, retrievedCount = retrievedDataChannel.PostAndReply(fun rc -> Get rc)
                let downloadedData, downloadedCount = downloadedChannel.PostAndReply(fun rc -> Get rc)

                let getUploadDate (date: DateTime option) = 
                    match date with 
                    | Some date -> date.ToString("dd/M/yyyy")
                    | None -> "N/A"
                       
                do match retrievedData with 
                    | Some videoData -> 
                        table.UpdateCell(0,1,$"{videoData.Title}")
                            .UpdateCell(0,2,$"{getUploadDate videoData.Upload}")
                            .UpdateCell(0,3,$"{retrievedCount}") |> ignore
                    | _-> ()          
                                      
                do match downloadedData with 
                    | Some (videoData,_) -> 
                        table.UpdateCell(1,1,$"{videoData.Title}")
                            .UpdateCell(1,2,$"{getUploadDate videoData.Upload}")
                            .UpdateCell(1,3,$"{downloadedCount}/{retrievedCount}") |> ignore
                    | _-> ()     
                                          
                ctx.Refresh()

                if not downloadTask.IsCompleted then
                    do! loop () 
                else 
                    ()
            }

            do! loop ()
        }))
    progressTask



let printSampleGrid data =
// Prettier to get the middle image
    let getMiddleImage (collection: _ list) =
        let midpoint = collection.Length/2
        let _, (path:string) = collection[midpoint]
        CanvasImage(path)

    let images = data |> List.splitInto 3
                |> List.map getMiddleImage
    let sampleGrid = Grid().AddColumns(3).AddRow(images[0],images[1],images[2])
    AnsiConsole.Write(sampleGrid)

let playlistItemToVideoData (item:Data.PlaylistItem ) =
    let nullUpload = item.Snippet.PublishedAt
    let upload = if nullUpload.HasValue then Some nullUpload.Value else None
    {Title=item.Snippet.Title.EscapeMarkup(); Url=item.Snippet.Thumbnails.Default__.Url; Upload= upload}


[<EntryPoint>]
let main argv =
    try 
        let channelName, path, apiKey, fromDate, displayImages = parseArgs argv

        let stopWatch = Stopwatch.StartNew()

        AnsiConsole.MarkupLine($"Downloading thumbnails from channel [bold #8be9fd]{channelName.EscapeMarkup()}[/] to path [bold #bd93f9]{path}[/]")
        let service, channelId = getIdAndService channelName apiKey |> Async.RunSynchronously

        AnsiConsole.MarkupLine($"Retrieved channel id [bold #ff79c6]{channelId}[/] for channel [bold #8be9fd]{channelName}[/] \n")

        let retrievedDataChannel = stackProcessor
        let downloadedChannel = stackProcessor

        let isAfterDate vid = match vid.Upload with
                              | Some value -> value > fromDate;
                              | _-> false
        
        let downloadTask =  (service, channelId) ||> getAllVideoPages 
                            |> AsyncSeq.map playlistItemToVideoData 
                            |> AsyncSeq.takeWhile isAfterDate 
                            |> AsyncSeq.map (fun x-> retrievedDataChannel.Post(Send x) |> ignore; x)
                            |> (fun images -> downloadAllImages images path) 
                            |> AsyncSeq.map (fun x-> downloadedChannel.Post(Send x) |> ignore; x)
                            |> AsyncSeq.toListAsync
                            |> Async.StartAsTask

        let printProgressTask = printProgressAsync downloadTask retrievedDataChannel downloadedChannel

        let result = downloadTask |> Async.AwaitTask |> Async.RunSynchronously
        printProgressTask |> Async.AwaitTask |> Async.RunSynchronously
        let _, downloadedCount = downloadedChannel.PostAndReply(fun rc -> Get rc)

        if displayImages then 
            printSampleGrid result

        let elapsedTime = (int)stopWatch.Elapsed.TotalMilliseconds
        AnsiConsole.MarkupLine($"Downloaded [bold #ff5555]{downloadedCount}[/] items from [bold #8be9fd]{channelName}[/] "
        + $"to path [bold #bd93f9]{path}[/] in [bold white]{elapsedTime}ms[/]") 
        
    with e -> 
        AnsiConsole.MarkupLine($"[bold #ff5555]Error occured[/]\r\n[bold #ffb86c]{e.Message.EscapeMarkup()}[/]\r\n[bold #f1fa8c]{e.StackTrace.EscapeMarkup()}[/]")
        raise e
    0