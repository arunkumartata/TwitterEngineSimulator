// module TwitterEngine
#if INTERACTIVE
#r "nuget: Akka.FSharp" 
#r "nuget: Akka.TestKit"
#r "nuget: Akka.Remote"
#r "nuget: Akka.Configuration" 
#r ".\\bin\\Debug\\netcoreapp3.1\\TwitterEngineSimulator.dll"
#endif
open System.Threading
open System
open Akka.FSharp
open Akka.Configuration
open Akka.Remote
open Akka.Actor
open MessageFormat
#load "DAO.fsx"
open DAO
open System.Collections.Concurrent
open System.Security.Cryptography
let configuration =
    // Configuration.parse(@"
    // akka {
    //     actor{
    //         provider = remote
    //     }
    //     remote {
    //         handshake-timeout = 10s
    //         dot-netty.tcp {
    //             port = 8080
    //             hostname = 127.0.0.1
    //             connection-timeout = 10s
    //         }
    //     }
    // }")
    Configuration.parse(@"akka {
            log-config-on-start : on
            stdout-loglevel : DEBUG
            loglevel : ERROR
            actor {
                provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
                debug : {
                    receive : on
                    autoreceive : on
                    lifecycle : on
                    event-stream : on
                    unhandled : on
                }
            }
           remote {
            handshake-timeout = 10s
            dot-netty.tcp {
                port = 8888
                hostname = 127.0.0.1
                connection-timeout = 10s
            }
        }
        }")
printfn "Configuration %A" configuration
let system = System.create "MySystem" <| configuration
let activeUsersProxyRefs = new ConcurrentDictionary<string,IActorRef>()
let userRSAParams = new ConcurrentDictionary<string,RSAParameters>();
let userChallengeMap = new ConcurrentDictionary<string,byte[]>();
let challengeQuestionMap = new ConcurrentDictionary<byte[],int64>();
let rngCrypto = new RNGCryptoServiceProvider()
let timerTask (timer:int) selfActor=
    async{
        do! Async.Sleep(timer)
        // let! token = Async.CancellationToken
        printfn "Sending self timer to %A" selfActor
        selfActor <! "Timer done"
    }


let checkStringMessageAndAct (msg:string) selfActor senderAddress (taskToken:CancellationTokenSource) (timerToken:CancellationTokenSource) =
    match msg with
        | "finishedTask" ->
            // Need to check whether to give priority in case both tasks complete at same time
            // printfn "Received finishedTask %A" selfActor 
            timerToken.Cancel()
            selfActor <! PoisonPill.Instance
        | "Timer done" ->
            printfn "Received Timer done %A" selfActor
            taskToken.Cancel()
            let replymsg = {Status=FAILED;Message="Server busy, retry after some time!"}
            senderAddress <! replymsg
            selfActor <! PoisonPill.Instance
        | _ -> 
            None |> ignore


let checkIfActiveUser (userId:string) requestId (senderAddress) =
        if not (isActiveUser userId) then
            let replymsg = {RequestId=requestId;Status=FORBIDDEN;Message=String.Format("User with Id '{0}' forbidden to tweet!",userId)}
            senderAddress <! replymsg
            false
        else
            true

let addToLocalProxyRefs (userId:string) (senderAddress:IActorRef) =
 if not (activeUsersProxyRefs.ContainsKey(userId)) then
    activeUsersProxyRefs.AddOrUpdate(userId,senderAddress,fun key oval -> senderAddress) |> ignore
    true
 else
    false

let removeFromLocalProxyRefs (userId:string) =
    let mutable value = null
    activeUsersProxyRefs.TryRemove(userId,ref value)

let addRSAParamsToLocalMap (userId:string) (publicParm:RSAParameters) =
//  if not (userRSAParams.ContainsKey(userId)) then
    userRSAParams.AddOrUpdate(userId,publicParm,fun key oval -> publicParm) |> ignore
    true
//  else
    // false
    
let generateNextChallengeKey() =
    let mutable byteArr: byte [] = Array.zeroCreate 32
    rngCrypto.GetBytes(byteArr)
    while challengeQuestionMap.ContainsKey(byteArr) do
        rngCrypto.GetBytes(byteArr)
    byteArr

let getUnixTime() =
    DateTimeOffset(DateTime.Now).ToUnixTimeSeconds()

let updateUserChallenge (userId:string) (ckey:byte[]) =
    userChallengeMap.AddOrUpdate(userId,ckey,fun key oval -> ckey) |> ignore
    true

let updateChallengeQuestion (ckey:byte[]) time =
    challengeQuestionMap.AddOrUpdate(ckey,time,fun key oval -> time) |> ignore
    true


let getUserActorRef (userId:string) =
    system.ActorSelection("akka.tcp://MySystem@127.0.0.1:8888/user/"+userId)

//Register API
let registerPerformTask (userId:string) passwd senderAddress (requestId:Guid) selfActor =
    async{
        if (isNull userId) || ((not (isNull userId)) && userId.Length =0) then
            let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg
        else
            let addedRow = addUser userId passwd
            if addedRow then
                let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("User with Id '{0}' created successfully!",userId)}
                senderAddress <! replymsg
            else
                let replymsg = {RequestId=requestId;Status=USEREXISTS;Message=String.Format("User with Id '{0}' already exists!",userId)}
                senderAddress <! replymsg
        selfActor <! "finishedTask"
    }

let RSAParamUpdateTask (rsaMsg:RSAParamMessage) senderAddress selfActor =
   async{
       if  (isNull rsaMsg.UserId) || (not (isUserExists rsaMsg.UserId)) then
            let replymsg = {RequestId=rsaMsg.RequestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg
       else 
            if (addRSAParamsToLocalMap rsaMsg.UserId rsaMsg.PublicKey) then
                let replymsg = {RequestId=rsaMsg.RequestId;Status=SUCCESS;Message=String.Format("Security info updated for user '{0}' successfully!",rsaMsg.UserId)}
                senderAddress <! replymsg
            else
               let replymsg = {RequestId=rsaMsg.RequestId;Status=USEREXISTS;Message=String.Format("Security info updated for user '{0}' failed!",rsaMsg.UserId)}
               senderAddress <! replymsg
       selfActor <! "finishedTask"
   }

let registerChildActor (mailbox:Actor<_>) =
    let ctsForTask = new CancellationTokenSource()
    let ctsForTimer = new CancellationTokenSource()
    let timeoutMilli = 5000
    let mutable originalSender = null
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match box msg with
        | :? CredentialsMessage as rmsg ->
            originalSender <- mailbox.Sender()
            Async.StartImmediate(registerPerformTask rmsg.UserId rmsg.Password originalSender rmsg.RequestId mailbox.Self,ctsForTask.Token)
            Async.StartImmediate(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? string as content ->
            checkStringMessageAndAct content mailbox.Self originalSender ctsForTask ctsForTimer
        | :? RSAParamMessage as rsamsg ->
                originalSender <- mailbox.Sender()
                Async.StartImmediate(RSAParamUpdateTask rsamsg originalSender mailbox.Self,ctsForTask.Token)
                Async.StartImmediate(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | _ -> 
            printfn "Invalid operation for registerChildActor"
        
        return! loop()
    }
    loop()

let registerActor (mailbox:Actor<_>) =
    let mutable cnt=1
    let getActorName idx =
        "ractor"+(string idx)
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        printfn "Received msg: %A" msg
        let actorRef = spawn system (getActorName cnt) <| registerChildActor
        actorRef.Forward msg
        // actorPool.[nextActor].Forward msg
        // updateNextActor()
        cnt <- cnt+1
        return! loop()
    }
    loop()
// register API ends

//Post Tweet API begins - It handles both original and retweet

let getActiveUsers (userList:array<string>) =
    let mutable activeUsrs = Array.empty
    for id in userList do
        if isActiveUser id then
            activeUsrs <- Array.append activeUsrs [|id|]
    activeUsrs


let postTweetToActiveUsers (userId:string) (tweetMsg:string) (tweetId:Guid) =
   async {
       let subscribedUsers = getSubscribesForUser userId
       let getUserMentioned = getRegexMatches tweetMsg userMentionRegex
       let mutable getActUsrs = getActiveUsers (Array.ofList subscribedUsers)
       getActUsrs <- Array.append getActUsrs (getActiveUsers (Array.ofList getUserMentioned))
       let tweet = getTweetsAlongWithUserId tweetId userId
       getActUsrs <- Array.distinct getActUsrs
       if tweet.Length >0 then
           for actUsr in getActUsrs do
            // let usr = getUserActorRef actUsr
            let usr = activeUsersProxyRefs.[actUsr]
            // printfn "%A %A" usr (tweet.Item(0))
            usr <! tweet.Item(0)
   }

let postTweetPerformTask (userId:string) (tweetMsg:string) (tweetId:Guid) requestId senderAddress selfActor =
    let mutable newGuid = Guid.Empty
    async {
        if (isNull userId) || userId.Length = 0 || (isNull tweetMsg && tweetId=Guid.Empty) 
            || ((not (isNull tweetMsg)) && tweetMsg.Length=0)  then
            let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg
        else
            if not (isNull tweetMsg) then
                newGuid <- Guid.NewGuid()
                let res = addTweet (newGuid) userId tweetMsg true
                if res then
                    let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("Your Tweet is successfully posted, tweetId - " + newGuid.ToString())}
                    senderAddress <! replymsg
                    Async.Start(postTweetToActiveUsers userId tweetMsg newGuid)
                else
                    let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Something went wrong while posting tweet!")}
                    senderAddress <! replymsg
            else if tweetId <> Guid.Empty then
                //Retweet logic
                let datarow = getTweetByID tweetId
                if datarow.Length <> 1 then
                    let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("No Tweet found with the given tweetID for retweet!")}
                    senderAddress <! replymsg
                else
                    let rowItems = datarow.[0].ItemArray
                    newGuid <- Guid.Parse(string rowItems.[0])
                    let res = addTweet (newGuid) userId (string rowItems.[2]) false
                    if res then
                        let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("Your ReTweet is successfully posted",userId)}
                        senderAddress <! replymsg
                        Async.Start(postTweetToActiveUsers userId (string rowItems.[2]) newGuid)
                    else
                        let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Something went wrong while posting tweet!")}
                        senderAddress <! replymsg
        selfActor <! "finishedTask"
    }       


let postTweetChildActor (mailbox:Actor<_>) =
    let ctsForTask = new CancellationTokenSource()
    let ctsForTimer = new CancellationTokenSource()
    let timeoutMilli = 500
    let mutable originalSender = null
    
    let rec loop() = actor {
        let! msg = mailbox.Receive()
        match box msg with
        | :? TweetMessage as tmsg ->
            let res = (checkIfActiveUser tmsg.UserId tmsg.RequestId (mailbox.Sender()))
            if not res then
                mailbox.Self <! PoisonPill.Instance
            else
                originalSender <- mailbox.Sender()
                //Async task
                Async.Start((postTweetPerformTask (tmsg.UserId.Trim()) (tmsg.Tweet.Trim()) Guid.Empty tmsg.RequestId originalSender mailbox.Self),ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? ReTweetMessage as rmsg ->
            let res = (checkIfActiveUser rmsg.UserId rmsg.RequestId (mailbox.Sender()))
            if not res then
                mailbox.Self <! PoisonPill.Instance
            else
                originalSender <- mailbox.Sender()
                Async.Start((postTweetPerformTask (rmsg.UserId.Trim()) null rmsg.TweetId rmsg.RequestId originalSender mailbox.Self),ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? String as content ->
            checkStringMessageAndAct content mailbox.Self originalSender ctsForTask ctsForTimer
            // if content = "finishedTask" then
            //     // logic to update active users
            //     None |> ignore
        | _ ->
            printfn "Invalid operation for postTweetChildActor"
        return! loop()
    }
    loop()

    
let postTweetActor (mailbox:Actor<_>) =
    let mutable cnt=1
    let getActorName idx =
        "pactor"+(string idx)

    let rec loop() = actor {
        let! msg = mailbox.Receive()
        printfn "TweetActor:Received msg: %A" msg
        let actorRef = spawn system (getActorName cnt) <| postTweetChildActor
        actorRef.Forward msg
        cnt <- cnt+1
        return! loop()
    }
    loop()

//PostTweet API ends


//Subscribe API
let subscribePerformTask (userId:string) (subscribedToId:string) requestId senderAddress selfActor =
    async{
        if (isNull userId) || (isNull subscribedToId) || userId = subscribedToId || userId.Length = 0 || subscribedToId.Length = 0 then
            let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg
        else
            let res = addSubscribedTo userId subscribedToId
            if res then
                let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("Subscribtion added for user '{0}' !",userId)}
                senderAddress <! replymsg
            else
                let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Something went wrong for subscribe API!")}
                senderAddress <! replymsg
        selfActor <! "finishedTask"
    }

let subscribeChildActor (mailbox:Actor<_>) =
    let ctsForTask = new CancellationTokenSource()
    let ctsForTimer = new CancellationTokenSource()
    let timeoutMilli = 500
    let mutable originalSender = null

    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match box msg with
        | :? SubscribeMessage as rmsg ->
            let res = (checkIfActiveUser rmsg.UserId rmsg.RequestId (mailbox.Sender()))
            if not res then
                mailbox.Self <! PoisonPill.Instance
            else
                originalSender <- mailbox.Sender()
                Async.Start(subscribePerformTask (rmsg.UserId.Trim()) (rmsg.SubscribedToId.Trim()) rmsg.RequestId originalSender mailbox.Self,ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? string as content ->
            checkStringMessageAndAct content mailbox.Self originalSender ctsForTask ctsForTimer
        | _ -> 
            printfn "Invalid operation for subscribeChildActor"
        
        return! loop()
    }
    loop()

let subscribeActor (mailbox:Actor<_>) =
    let mutable cnt=1
    let getActorName idx =
        "sactor"+(string idx)

    let rec loop() = actor {
        let! msg = mailbox.Receive()
        printfn "Subscribe:Received msg: %A" msg
        let actorRef = spawn system (getActorName cnt) <| subscribeChildActor
        actorRef.Forward msg
        cnt <- cnt+1
        return! loop()
    }
    loop()

//Subscribe API ends

//Query API
let queryPerformTask (userId:string) (data:string) (queryType:TagType) requestId senderAddress selfActor =
    async {
        if (isNull userId) || (isNull data && queryType=TagType.HashTag) then
            let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg

        else
            if queryType = TagType.HashTag then
                let tweetsInfo = getTweetsByHashTag [data]
                let replyMsg = {RequestId=requestId;Status=SUCCESS;Content=tweetsInfo}
                senderAddress <! replyMsg
            else if queryType = TagType.UserMention then
                let tweetsInfo = getTweetsByUserMention userId
                let replyMsg = {RequestId=requestId;Status=SUCCESS;Content=tweetsInfo}
                senderAddress <! replyMsg
            else if queryType = TagType.Subscribed then
                let subscribedToList = getSubscribedToForUser userId
                let tweetsInfo = tweetsByUserIdList subscribedToList
                let replyMsg = {RequestId=requestId;Status=SUCCESS;Content=tweetsInfo}
                senderAddress <! replyMsg
        selfActor <! "finishedTask"
    }

let queryChildActor (mailbox:Actor<_>) =
    let ctsForTask = new CancellationTokenSource()
    let ctsForTimer = new CancellationTokenSource()
    let timeoutMilli = 500
    let mutable originalSender = null
    let rec loop() = actor {
        let! msg = mailbox.Receive()
        match box msg with
        | :? QueryMessage as qhmsg ->
            let res = (checkIfActiveUser qhmsg.UserId qhmsg.RequestId (mailbox.Sender()))
            if not res then
                mailbox.Self <! PoisonPill.Instance
            else
                originalSender <- mailbox.Sender()
                Async.Start((queryPerformTask qhmsg.UserId qhmsg.Data qhmsg.QueryType qhmsg.RequestId originalSender mailbox.Self),ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
            None |> ignore
        | :? String as content ->
            checkStringMessageAndAct content mailbox.Self originalSender ctsForTask ctsForTimer
        | _ ->
            printfn "Invalid operation for queryChildActor"
        return! loop()
    }
    loop()

let queryActor (mailbox:Actor<_>) =
    let mutable cnt=1
    let getActorName idx =
        "qactor"+(string idx)

    let rec loop() = actor {
        let! msg = mailbox.Receive()
        printfn "QueryActor: Received msg: %A" msg
        let actorRef = spawn system (getActorName cnt) <| queryChildActor
        actorRef.Forward msg
        cnt <- cnt+1
        return! loop()
    }
    loop()


//Login API
let loginPerformTask (userId:string) passwd senderAddress requestId selfActor =
    async{
        if isActiveUser userId then
            let replymsg = {RequestId=requestId;Status=USEREXISTS;Message=String.Format("User with Id '{0}' already logged in!",userId)}
            senderAddress <! replymsg
        else
            let authenticated = validateUser userId passwd
            if authenticated then
                // see whether we need to extra validation
                addUserToLocalMap userId |> ignore
                addToLocalProxyRefs userId senderAddress |> ignore
                let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("User with Id '{0}' logged in successfully!",userId)}
                senderAddress <! replymsg
            else
                let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("User name or password is wrong!")}
                senderAddress <! replymsg
        selfActor <! "finishedTask"
    }

let logoutPerformTask (userId:string) requestId senderAddress selfActor =
    async{
        if not (isActiveUser userId) then
            let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("User with Id '{0}' already logged out!",userId)}
            senderAddress <! replymsg
        else
            if removeUserFromLocalMap userId then
                removeFromLocalProxyRefs userId |> ignore
                // see whether we need to extra validation
                let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("User Id '{0}' logged out successfully!",userId)}
                senderAddress <! replymsg
            else
                let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("User was not able to logout , userId : '{0}'", userId)}
                senderAddress <! replymsg
        selfActor <! "finishedTask"
    }

let loginThroughChallengeTask (userId:string) (challengeResp:ChallengeResponse) senderAddress requestId selfActor isResponse =
    async{
        if (isNull userId) || ((not <| isNull userId) && userId.Length=0) || (not (isUserExists userId)) then
            let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Invalid data sent. Please check and retry")}
            senderAddress <! replymsg
        elif isActiveUser userId then
            let replymsg = {RequestId=requestId;Status=USEREXISTS;Message=String.Format("User with Id '{0}' already logged in!",userId)}
            senderAddress <! replymsg
        else
            if isResponse then
            //verify challenge
                let rsaCrypto = new RSACryptoServiceProvider()
                rsaCrypto.ImportParameters(userRSAParams.[userId])
                if ((challengeResp.Time-challengeQuestionMap.[(userChallengeMap.[userId])])<1L) && rsaCrypto.VerifyData(userChallengeMap.[userId],challengeResp.SignedData,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1) then
                    addUserToLocalMap userId |> ignore
                    addToLocalProxyRefs userId senderAddress |> ignore
                    let replymsg = {RequestId=requestId;Status=SUCCESS;Message=String.Format("User with Id '{0}' logged in successfully!",userId)}
                    senderAddress <! replymsg
                else
                    let replymsg = {RequestId=requestId;Status=FAILED;Message=String.Format("Either the challenge is not received within time or data not valid. Please retry login!")}
                    senderAddress <! replymsg
            else
            //generate challenge
                let cKey = generateNextChallengeKey()
                let challengemsg = {RequestId=Guid.NewGuid();UserId=userId;ChallengeKey=cKey}
                let sentTime = getUnixTime()
                updateChallengeQuestion cKey sentTime |> ignore
                updateUserChallenge userId cKey |> ignore
                senderAddress <! challengemsg
        selfActor <! "finishedTask"
    }

let loginlogoutChildActor (mailbox:Actor<_>) =
    let ctsForTask = new CancellationTokenSource()
    let ctsForTimer = new CancellationTokenSource()
    let timeoutMilli = 500
    let mutable originalSender = null
    let dummyChallenge = {RequestId=Guid.Empty;UserId="";ChallengeKey=null;Time=0L;SignedData=null}
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match box msg with
        | :? CredentialsMessage as rmsg ->
            originalSender <- mailbox.Sender()
            if (not <| isNull rmsg.UserId) && userRSAParams.ContainsKey(rmsg.UserId) then
                Async.Start(loginThroughChallengeTask rmsg.UserId dummyChallenge originalSender rmsg.RequestId mailbox.Self false,ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
            else
                Async.Start(loginPerformTask rmsg.UserId rmsg.Password originalSender rmsg.RequestId mailbox.Self,ctsForTask.Token)
                Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? LogoutMessage as rmsg ->
            originalSender <- mailbox.Sender()
            Async.Start(logoutPerformTask rmsg.UserId rmsg.RequestId originalSender mailbox.Self,ctsForTask.Token)
            Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? ChallengeResponse as cmsg ->
            originalSender <- mailbox.Sender()
            Async.Start(loginThroughChallengeTask cmsg.UserId cmsg originalSender cmsg.RequestId mailbox.Self true,ctsForTask.Token)
            Async.Start(timerTask timeoutMilli mailbox.Self,ctsForTimer.Token)
        | :? string as content ->
            checkStringMessageAndAct content mailbox.Self originalSender ctsForTask ctsForTimer
        | _ -> 
            printfn "Invalid operation for loginlogoutChildActor"
        
        return! loop()
    }
    loop()

let loginActor (mailbox:Actor<_>) =
    let mutable cnt=1
    let getActorName idx =
        "lactor"+(string idx)

    let rec loop() = actor {
        let! msg = mailbox.Receive()
        printfn "Login: Received msg: %A" msg
        let actorRef = spawn system (getActorName cnt) <| loginlogoutChildActor
        actorRef.Forward msg
        cnt <- cnt+1
        return! loop()
    }
    loop()
//Login API ends
// let actorRef = spawn system "registerActor" <| registerActor
// let loginActorRef = spawn system "loginActor" <| loginActor
// let subscribeActorRef = spawn system "subscribeActor" <| subscribeActor
// let tweetActorRef = spawn system "tweetActor" <| postTweetActor
// let queryActorRef = spawn system "queryActor" <| queryActor

//printfn "Server Started, waiting for connection"

//Console.ReadLine() 