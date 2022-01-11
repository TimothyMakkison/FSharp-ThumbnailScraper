open System
open Google.Apis.Services
open System.IO
open Google.Apis.YouTube.v3
open System.Text.RegularExpressions
open FSharp.Control
open Argu
open Spectre.Console
open System.Threading.Tasks

let apiKey = File.ReadAllText("./google-api-key.txt")

type CommandArguments = 
        |[<Mandatory>] Save_Path of path:string
        |[<Mandatory>] Channel_Name of channel:string
        | Api_Key of api_key:string 
        | After_Date of from_date:string 

        interface IArgParserTemplate with
              member s.Usage =
                  match s with
                  | Save_Path _ -> "specify the save location."
                  | Channel_Name _ -> "specify the youtube channel to scrape."
                  | After_Date _ -> "specify the date from which uploads will be collected."
                  | Api_Key _ -> "specify the google api key."

type VideoData = {Title:string; Url:string; Upload: DateTime option}

module ImageDownloading = 
    open System.Net.Http

    let getFileExtension url = 
        let uri = System.Uri(url)
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
    
        images |> AsyncSeq.mapAsyncParallel (fun x -> downloadImage x folderPath client)

module YoutubeCallers = 
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

    let getChannelUploadsPlaylistId (service:YouTubeService) channelName = async{
        let mutable channelsListRequest:ChannelsResource.ListRequest = service.Channels.List("contentDetails")
        channelsListRequest.ForUsername <- channelName;

        let! response =  channelsListRequest.ExecuteAsync() |> Async.AwaitTask

        let channel = response.Items :> seq<_> |> Seq.head
        let playlistId = channel.ContentDetails.RelatedPlaylists.Uploads
        return playlistId
    }

    let getIdAndService channelName = async {
        //Create service and get channel id
        let baseClient = new BaseClientService.Initializer(ApiKey = apiKey, ApplicationName = "ThumbnailDownloader") 
        let service = new YouTubeService(baseClient)

        let! playlistId = getChannelUploadsPlaylistId service channelName 
        return (service, playlistId)
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
                    | Send newState -> return! loop (Some newState) (counter + 1)
                    | Get channel -> 
                        channel.Reply(latestState, counter) 
                        return! loop latestState counter
                }
            loop None 0
            )

open StackCounterMailbox

let playlistItemToVideoData (item:Data.PlaylistItem ) =
    let nullUpload = item.Snippet.PublishedAt
    let upload = if nullUpload.HasValue then Some nullUpload.Value else None
    {Title=item.Snippet.Title; Url=item.Snippet.Thumbnails.Default__.Url; Upload= upload}

let parseArgs arg = 
    let parser = ArgumentParser.Create<CommandArguments>()
    let results = parser.Parse(inputs = arg, raiseOnUsage = true) 
    
    let channelName = results.GetResult Channel_Name
    let path = results.GetResult Save_Path
    //let apiKey = results.GetResult Api_Key
    
    let fromDate = results.GetResult(After_Date, defaultValue = "12/1/2000 9:00:00 AM") |> DateTime.Parse 
    (channelName, path, fromDate)


let createProgressTable = 
    let table = Table().HeavyBorder()
                    .AddColumn("[bold #f1fa8c]Process[/]") 
                    .AddColumn("[bold #f1fa8c]Title[/]") 
                    .AddColumn("[bold #f1fa8c]Upload[/]") 
                    .AddColumn("[bold #f1fa8c]Count[/]")
                    .AddRow("Most recently retrieved:","","","0")
                    .AddRow("Most recently downloaded:","","","0/0");

    table.Columns[1].Width <- 50
    table.Columns[2].Width <- 15
    table

let progressTask (downloadTask:Task) (retrievedDataChannel: MailboxProcessor<StackProcessorMessage<VideoData>>) (downloadedChannel:MailboxProcessor<StackProcessorMessage<VideoData * string>>) = 
    let table = createProgressTable

    let progressTask = AnsiConsole.Live(table).StartAsync((fun ctx -> 
        task{
            let timeSpan = TimeSpan.FromMilliseconds(100)

            let rec loop () = async{
                do! Task.Delay(timeSpan)|> Async.AwaitTask |> Async.Ignore

                let retrievedData, retrievedCount = retrievedDataChannel.PostAndReply(fun rc -> Get rc)
                let downloadedData, downloadedCount = downloadedChannel.PostAndReply(fun rc -> Get rc)

                let printUpload (date: DateTime option) = 
                    match date with 
                    | Some date -> date.ToString("dd/M/yyyy")
                    | None -> "N/A"
                       
                do match retrievedData with 
                    | Some videoData -> 
                        table.UpdateCell(0,1,$"{videoData.Title}")
                            .UpdateCell(0,2,$"{printUpload videoData.Upload}")
                            .UpdateCell(0,3,$"{retrievedCount}") |> ignore
                    | _-> ()          
                                      
                do match downloadedData with 
                    | Some (videoData,savePath) -> 
                        table.UpdateCell(1,1,$"{videoData.Title}")
                            .UpdateCell(1,2,$"{printUpload videoData.Upload}")
                            .UpdateCell(1,3,$"{downloadedCount}/{retrievedCount}") |> ignore
                    | _-> ()     
                                          
                ctx.Refresh()

                if not downloadTask.IsCompleted then
                    loop () |> Async.RunSynchronously
                else 
                    ()
            }

            loop () |> Async.RunSynchronously
        }))
    progressTask

[<EntryPoint>]
let main argv =
    let channelName, path, fromDate = parseArgs argv

    let stopWatch = System.Diagnostics.Stopwatch.StartNew()

    let service, channelId = YoutubeCallers.getIdAndService channelName |> Async.RunSynchronously

    AnsiConsole.MarkupLine($"Retrieved channel id [bold #ff79c6]{channelId}[/] for [bold #8be9fd]{channelName}[/]")
    AnsiConsole.MarkupLine($"Downloading [bold white]thumbnails[/] from [bold #8be9fd]{channelName}[/] to path [bold #bd93f9]{path}[/]")
    AnsiConsole.WriteLine()

    let retrievedDataChannel = stackProcessor
    let downloadedChannel = stackProcessor

    let isAfterDate vid = match vid.Upload with
                          | Some value -> value > fromDate;
                          | _-> false
    
    let downloadTask =  (service, channelId) ||> YoutubeCallers.getAllVideoPages 
                        |> AsyncSeq.map playlistItemToVideoData 
                        |> AsyncSeq.takeWhile isAfterDate 
                        |> AsyncSeq.map (fun x-> retrievedDataChannel.Post(Send x) |> ignore; x)
                        |> (fun images -> ImageDownloading.downloadAllImages images path) 
                        |> AsyncSeq.map (fun x-> downloadedChannel.Post(Send x) |> ignore; x)
                        |> AsyncSeq.toListAsync

    let startedDownloadTask = downloadTask |> Async.StartAsTask
    let progress = progressTask startedDownloadTask retrievedDataChannel downloadedChannel

    let result = startedDownloadTask |> Async.AwaitTask |> Async.RunSynchronously
    progress |> Async.AwaitTask |> Async.RunSynchronously
    let _, downloadedCount = downloadedChannel.PostAndReply(fun rc -> Get rc)
    
    AnsiConsole.MarkupLine($"Downloaded [bold #ff5555]{downloadedCount}[/] items from [bold #8be9fd]{channelName}[/] "
    + $"to path [bold #bd93f9]{path}[/] in [bold white]{(int)stopWatch.Elapsed.TotalMilliseconds}ms[/]")
    
    0

// Tasks:
// ✓ move into modules
// ✓ get channel id and then do pagiantion
// ✓ update colors
// ✓ add columns
// add image display
// ✓ print elapsed time
// ✓ take api key from args
// wrap in try catch
// move prorgress into separate function