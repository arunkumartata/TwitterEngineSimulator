
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
#load "TwitterEngine.fsx"
open System.Threading
open System
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
open System.Net

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

open Newtonsoft.Json

open TwitterEngine
open MessageFormat

//Login API ends
let registerActorRef = spawn system "registerActor" <| registerActor
let loginActorRef = spawn system "loginActor" <| loginActor
let subscribeActorRef = spawn system "subscribeActor" <| subscribeActor
let tweetActorRef = spawn system "tweetActor" <| postTweetActor
let queryActorRef = spawn system "queryActor" <| queryActor
let mutable cnt=1

let settings = new JsonSerializerSettings(TypeNameHandling = TypeNameHandling.All)
settings.Converters.Add(Converters.StringEnumConverter())

let sendws (webSocket : WebSocket) (context: HttpContext) (data:string) =
    let dosomething () = socket {
        // printfn "in send socket!"
        let byteResponse =
                data
                |> System.Text.Encoding.ASCII.GetBytes
                |> ByteSegment
        do! webSocket.send Text byteResponse true
    }
    dosomething ()

let task (webSocket : WebSocket) (context: HttpContext) (data:string)=
    //printfn "Started something"
    async{
        let! ws = sendws webSocket context (data)
        ws
        }

let getActorName idx =
    "proxyactor"+(string idx)

let proxyActor (clientSocket:WebSocket) (clientContext:HttpContext) (mailbox:Actor<_>) =
    let mutable userId =""
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        printfn "Received msg proxyActor: %A" msg
        match box msg with
        | :? String as content ->
            match content with
            | "selfkill" ->
                mailbox.Self <! PoisonPill.Instance
            | _ ->
                try
                    let requestLines = content.Split("\n")
                    let requestType = 
                        if requestLines.[0].Split(":").[0] = "RequestType" then
                            (requestLines.[0].Split(":").[1].Trim('"'))
                        else 
                            ""
                    let requestURL = 
                        if requestLines.[1].Split(":").[0] = "RequestURL" then
                            (requestLines.[1].Split(":").[1].Trim('"'))
                        else 
                            ""
                    let body = 
                        if requestLines.[2].Split(":").[0] = "Body" then
                            printfn "%A" (requestLines.[2].Trim('"'))
                            ((requestLines.[2].Substring(requestLines.[2].IndexOf(':') + 1)).Trim('"'))
                        else 
                            ""

                    printfn "Type = %A \n URL = %A \n Body = %A\n" requestType requestURL body
                    // let content = JsonConvert.DeserializeObject<CredentialsMessage>(body, settings)
                    
                    if requestType <> "" && requestURL <> "" then
                        if requestURL = "register" && requestType = "POST" then
                            let registerMsg = JsonConvert.DeserializeObject<CredentialsMessage>(body, settings)
                            registerActorRef <! registerMsg
                        else if (requestURL = "login") && requestType = "POST" then
                            let loginMsg = JsonConvert.DeserializeObject<CredentialsMessage>(body, settings)
                            userId <- loginMsg.UserId
                            loginActorRef <! loginMsg
                        else if requestURL = "subscribe" && requestType = "POST" then
                            let subscribeMsg = JsonConvert.DeserializeObject<SubscribeMessage>(body, settings)
                            subscribeActorRef <! subscribeMsg
                        else if requestURL = "tweet" && requestType = "POST" then
                            let tweetMsg = JsonConvert.DeserializeObject<TweetMessage>(body, settings)
                            tweetActorRef <! tweetMsg
                        else if requestURL = "query" && requestType = "GET" then
                            let queryMsg = JsonConvert.DeserializeObject<QueryMessage>(body, settings)
                            queryActorRef <! queryMsg
                        else if requestURL = "logout" && requestType = "POST" then
                            let logoutMsg = JsonConvert.DeserializeObject<LogoutMessage>(body, settings)
                            loginActorRef <! logoutMsg
                        else if requestURL = "retweet" && requestType = "POST" then
                            let retweetMsg = JsonConvert.DeserializeObject<ReTweetMessage>(body, settings)
                            tweetActorRef <! retweetMsg
                        else if requestURL = "subscribe" && requestType = "POST" then
                            let subsMsg = JsonConvert.DeserializeObject<ReTweetMessage>(body, settings)
                            subscribeActorRef <! subsMsg
                        else if requestURL = "seckeyupdate" && requestType = "POST" then
                            let securityMsg = JsonConvert.DeserializeObject<RSAParamMessage>(body,settings)
                            registerActorRef <! securityMsg
                        else if requestURL = "challengeresponse" && requestType ="POST" then
                            let challResp = JsonConvert.DeserializeObject<ChallengeResponse>(body,settings)
                            loginActorRef <! challResp
                        else 
                            raise (System.Exception("No suitable endpoint for method"))
                    else
                        raise (System.Exception("requestType or requestURL empty"))
                with
                | :? Exception as ex ->
                    let res =  sprintf "500 - Internal Server Error. Exception -%A" ex
                    Async.StartImmediate(task clientSocket clientContext res)
        
        | :? ReplyMessage as rmsg ->
            // printfn "proxy - %A" rmsg
            let response = JsonConvert.SerializeObject(rmsg)
            Async.StartImmediate(task clientSocket clientContext response)
        | :? TweetsResponse as tweetsResp ->
            let response = JsonConvert.SerializeObject(tweetsResp)
            Async.StartImmediate(task clientSocket clientContext response)
        | :? TweetData as tdata ->
            let response = JsonConvert.SerializeObject(tdata)
            Async.StartImmediate(task clientSocket clientContext response)
        | :? ChallengeMessage as cmsg ->
            let response = JsonConvert.SerializeObject(cmsg)
            Async.StartImmediate(task clientSocket clientContext response)
        | _ ->
            None |> ignore
        return! loop()
    }
    loop()

let ws (webSocket : WebSocket) (context: HttpContext) =
  socket {  
    // if `loop` is set to false, the server will stop receiving messages
    let mutable loop = true
    // Async.StartImmediate((task webSocket context "something"))
    let localActorRef = spawn system (getActorName cnt) <| (proxyActor webSocket context)
    cnt <- cnt+1
    while loop do
      // the server will wait for a message to be received without blocking the thread
      let! msg = webSocket.read()
      
      match msg with
      | (Text, data, true) ->
        localActorRef <! (UTF8.toString data)

      | (Close, _, _) ->
        printfn "Closing socket : %A" socket 
        let emptyResponse = [||] |> ByteSegment
        localActorRef <! "selfkill"
        do! webSocket.send Close emptyResponse true
        loop <- false

      | _ -> ()
    }

/// An example of explictly fetching websocket errors and handling them in your codebase.
let wsWithErrorHandling (webSocket : WebSocket) (context: HttpContext) = 
   
   let exampleDisposableResource = { new IDisposable with member __.Dispose() = printfn "Resource needed by websocket connection disposed" }
   let websocketWorkflow = ws webSocket context
   
   async {
    let! successOrError = websocketWorkflow
    match successOrError with
    // Success case
    | Choice1Of2() -> ()
    // Error case
    | Choice2Of2(error) ->
        // Example error handling logic here
        printfn "Error: [%A]" error
        exampleDisposableResource.Dispose()
        
    return successOrError
   }

let app : WebPart = 
  choose [
    path "/api" >=> handShake ws
    GET >=> choose [ path "/" >=> file "index.html"; browseHome ]
    NOT_FOUND "Found no handlers." ]

// [<EntryPoint>]
// let main _ =
startWebServer { defaultConfig with logger = Targets.create Verbose [||] } app
  // 0
Console.ReadLine()

//
// The FIN byte:
//
// A single message can be sent separated by fragments. The FIN byte indicates the final fragment. Fragments
//
// As an example, this is valid code, and will send only one message to the client:
//
// do! webSocket.send Text firstPart false
// do! webSocket.send Continuation secondPart false
// do! webSocket.send Continuation thirdPart true
//
// More information on the WebSocket protocol can be found at: https://tools.ietf.org/html/rfc6455#page-34
//