
#if INTERACTIVE
#time "on"
//#r "nuget: FSharp.Core" 
#r "nuget: Suave"
#r "nuget: Akka.FSharp" 
#r "nuget: Akka.TestKit"
#r "nuget: Akka.Remote"
#r "nuget: Akka.Configuration" 
#r ".\\bin\\Debug\\netcoreapp3.1\\TwitterEngineSimulator.dll"
#endif
open System
open FSharp.Data
open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open Akka.FSharp
open Akka.Configuration
open Akka.Remote
open Akka.Actor

open System
open System.Threading
open System.Text.Encodings
open System.Net.WebSockets

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

open Newtonsoft.Json
open MessageFormat
open System.Security.Cryptography

let settings = new JsonSerializerSettings(TypeNameHandling = TypeNameHandling.All)
let serverUrl = Uri("ws://127.0.0.1:8080/api")
let ws = new ClientWebSocket()
let cts = new CancellationTokenSource()
Async.AwaitTask(ws.ConnectAsync(serverUrl,cts.Token))

let mutable user = ""
let mutable userLoginTry = ""
let mutable userLoginRequestId = Guid.Empty
let mutable registerRequestId = Guid.Empty
let userRSA = RSA.Create()
let publickey = userRSA.ExportParameters(false)
let getRestMessage requestType requestMethod data = 
    let jsonObj = JsonConvert.SerializeObject(data,settings)
    let regMsg= sprintf "RequestType:%A\nRequestURL:%A\nBody:%A\n" requestType requestMethod jsonObj
        //printfn "Messgae to be sent : %A \n original data : %A" regMsg data
    regMsg
let getUnixTime() =
    DateTimeOffset(DateTime.Now).ToUnixTimeSeconds()

let receiveTask (webSocket:ClientWebSocket) =
    async {
        while true do
            let mutable data = Array.zeroCreate 4096
            let! tmp = Async.AwaitTask(ws.ReceiveAsync(ArraySegment(data),CancellationToken.None))
            let response = ((UTF8.toString data).TrimEnd [| '\u0000' |])
            printfn "\n%A" response
            let challangeMsg = JsonConvert.DeserializeObject<ChallengeMessage>(response,settings)
            if (not <| isNull challangeMsg.ChallengeKey) then
                // Thread.Sleep(1000)
                printfn "Sending signed challenge response to Server..."
                let sdata = userRSA.SignData(challangeMsg.ChallengeKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)
                let challResp = {RequestId=challangeMsg.RequestId;UserId=user;ChallengeKey=challangeMsg.ChallengeKey;Time=getUnixTime();SignedData=sdata}
                Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "challengeresponse" challResp)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
            let responseMsg = JsonConvert.DeserializeObject<ReplyMessage>(response, settings)
            if (responseMsg.Status = 200 || responseMsg.Status = 409 ) && (responseMsg.RequestId = userLoginRequestId || responseMsg.RequestId = registerRequestId) then
                user <- userLoginTry
    }
Thread.Sleep(300)
Async.StartImmediate(receiveTask ws) |> ignore

let mutable flag = true
printfn "Available API's:\n 1. register\n 2. login\n 3. logout\n 4. tweet\n 5. query\n 6. retweet\n 7. subscribe\n 8. updatesecuritykey\n 9. quit"

while flag do
    Thread.Sleep(300)
    printf "Enter the operation name from above>"
    let input = Console.ReadLine()
    if input.Trim() = "register" then
        printf "Enter the user name to be registered: "
        let userId = Console.ReadLine()
        printf "Enter the password:"
        let password = Console.ReadLine()
        userLoginTry <- userId
        let registerMsg ={RequestId=Guid.NewGuid();UserId=userId;Password=password}
        registerRequestId <- registerMsg.RequestId
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "register" registerMsg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
    elif input.Trim()= "updatesecuritykey" then
        let secMsg ={RequestId=Guid.NewGuid();UserId=user;PublicKey=publickey}
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "seckeyupdate" secMsg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
    elif input.Trim() ="login" then
        printf "Enter the user name to be login: "
        let userId = Console.ReadLine()
        printf "Enter the password:"
        let password = Console.ReadLine()
        userLoginTry <- userId
        let registerMsg ={RequestId=Guid.NewGuid();UserId=userId;Password=password};
        userLoginRequestId <- registerMsg.RequestId
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "login" registerMsg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
        Thread.Sleep(1200)
    elif input.Trim() = "logout" then
        // printf "Enter the user name to be logout: "
        // let user = Console.ReadLine()
        let registerMsg ={RequestId=Guid.NewGuid();UserId=user};
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "logout" registerMsg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
        
    elif input.Trim() = "tweet" then
        // printf "Enter the user name from which to tweet : "
        // let user = Console.ReadLine()
        printf "Enter the tweet: "
        let tweet = Console.ReadLine()
        let msg ={RequestId=Guid.NewGuid();UserId=user;Tweet=tweet};
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "tweet" msg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
        
    elif input.Trim() = "query" then
        // printf "Enter the user name from which to query : "
        // let user = Console.ReadLine()
        printf "Enter the query type (mentions | hashtag | subscribers): "
        let mutable queryType = Console.ReadLine() 
        let mutable gotTagType = false
        let mutable tagType = TagType.HashTag
        let mutable query = null
        while (not gotTagType) do
            gotTagType <- true
            if queryType = "mentions" then
                tagType <- TagType.UserMention
            elif queryType = "hashtag" then
                tagType <- TagType.HashTag
                printf "Enter the query : "
                query <- Console.ReadLine()
            elif queryType = "subscribers" then
                tagType <- TagType.Subscribed 
            else 
                printfn "Mention correct query type"
                queryType <- Console.ReadLine()
                gotTagType <- false
        
        // printf "Enter the query : "
        // let query = Console.ReadLine()
        // type TagType = HashTag = 0 | UserMention = 1 | Subscribed = 2
        // type QueryMessage = {RequestId:Guid;UserId:string;Data:string; QueryType:TagType}
        let msg ={RequestId=Guid.NewGuid();UserId=user;Data=query; QueryType=tagType}
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "GET" "query" msg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
    elif input.Trim() = "subscribe" then
        // printf "Enter the user name from which to subscribe : "
        // let user = Console.ReadLine()
        printf "Enter the user name you want to subscribe : "
        let subsTo = Console.ReadLine()
        let msg ={RequestId=Guid.NewGuid();UserId=user;SubscribedToId=subsTo};
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "subscribe" msg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
          
    elif input.Trim() = "quit" then
        flag <- false
    elif input.Trim() = "retweet" then
        // printf "Enter the user name from which to retweet : "
        // let user = Console.ReadLine()
        printf "Enter the tweet id to retweet: "
        let tweetId = System.Guid.Parse(Console.ReadLine())
        let msg ={RequestId=Guid.NewGuid();UserId=user;TweetId=tweetId};
        Async.AwaitTask(ws.SendAsync(ArraySegment(System.Text.Encoding.UTF8.GetBytes (getRestMessage "POST" "retweet" msg)),WebSocketMessageType.Text,true,CancellationToken.None)) |> ignore
        
    else
        printfn "%A -%A" "Invalid operation type given" input

// Console.ReadLine()