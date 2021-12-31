open Google.Apis.Services
open System.IO
open Google.Apis.YouTube.v3
open System.Threading.Tasks
open System.Net.Http
open System.Text.RegularExpressions
open System

let apiKey = File.ReadAllText("./google-api-key.txt")
let saveLocation = @"C:\Users\timma\Desktop\ImageData\ColorThumbnails\";

type VideoData = {Title:string; Url:string}

let getFileExtension url = 
    let uri = url  |> Uri
    uri.GetLeftPart(UriPartial.Path) |> Path.GetExtension

let getPath folder title url = 
    let fileName = Regex.Replace(title, @"[\\/:*?""<>|]", "")
    let fileExtension = getFileExtension url

    Path.Combine(folder, $"{fileName}{fileExtension}")

let downloadImage title url folder (client:HttpClient) = 
    async {
        let path = getPath folder title url

        let! imageBytes = client.GetByteArrayAsync(url) |> Async.AwaitTask
        return! File.WriteAllBytesAsync(path, imageBytes) |> Async.AwaitTask
    }

let downloadAllImages images folderPath = 
    task{
        let client = new HttpClient()
        Directory.CreateDirectory(folderPath) |> ignore

        return! images |> Seq.map (fun x -> downloadImage x.Title x.Url folderPath client  ) |> Async.Parallel |> Async.Ignore;
    }
    


let playlistItemToVideoData (item:Data.PlaylistItem )= {Title=item.Snippet.Title; Url=item.Snippet.Thumbnails.Default__.Url}

let getAllPages service playlistId = 

    let rec getPage (service:YouTubeService) playlistId pageToken (data :seq<VideoData>) = 
        task {
            if pageToken = null then   
                return data
            else
                let mutable request = service.PlaylistItems.List("snippet")

                request.PlaylistId <- playlistId
                request.PageToken <- pageToken
                request.MaxResults <- 50L

                let! response = request.ExecuteAsync() 

                let pageData = response.Items :> seq<_> |> Seq.map playlistItemToVideoData

                printfn "Retrieved %d items with page token %s." (pageData |> Seq.length) pageToken

                let nextToken= response.NextPageToken
                let pageData = pageData |> Seq.append data

                return! getPage service playlistId nextToken pageData
    }

    getPage service playlistId "" Seq.empty

let downloadImages channelName = task {

    //Create service and get channel id
    let baseClient = new BaseClientService.Initializer(ApiKey = apiKey, ApplicationName= "ThumbnailDownloader") 
    let service = new YouTubeService(baseClient)

    let mutable channelsListRequest:ChannelsResource.ListRequest  = service.Channels.List("contentDetails")
    channelsListRequest.ForUsername <- channelName;

    let! response =  channelsListRequest.ExecuteAsync() 

    let channel = response.Items :> seq<_> |> Seq.head
    let playlistId = channel.ContentDetails.RelatedPlaylists.Uploads
    printfn "Retrieved id: %s for channel: %s" playlistId channelName

    let! pages = getAllPages service playlistId

    // Print all pages
    printfn "%A" (Seq.toList pages)
    printfn "Retrieved %d items from the channel %s" (pages |> Seq.length) channelName
    
    return pages
}

[<EntryPoint>]
let main args =
    let stopWatch = System.Diagnostics.Stopwatch.StartNew()

    //let channelName = "CloudKidOfficial"

    //let task = downloadImages channelName
    //printfn "%f" stopWatch.Elapsed.TotalMilliseconds
    let client = new HttpClient()
    
    let url = @"https://pbs.twimg.com/media/FHs5cUQXIAACYYI?format=jpg&name=large"
    let path = @"C:\Users\timma\Desktop\coding\F#\ThumbnailScraper\ThumbnailScraper\"
    downloadImage "The_Doc" url path client |> Async.RunSynchronously

    0


//Should 
//Take file path, url and config
//Check valid path

//Go to url
//Collect all videos
//Foreach extract thumbnail

//go to next page