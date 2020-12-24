module DAO

#if INTERACTIVE
#r ".\\bin\\Debug\\netcoreapp3.1\\TwitterEngineSimulator.dll"
#endif
open System
open System.Data
open System.Text.RegularExpressions
open System.Collections.Concurrent
open MessageFormat


//type TweetData = {Id:Guid; UserId:string;Data:string; IsOriginal:Boolean; Time:DateTime}

let mutable userMap:Map<string, string> = Map.empty
let loggedInUser = new ConcurrentDictionary<string,string>()
let hashtagRegex = @"#(\w+)"
let userMentionRegex = @"@(\w+)"


let getRegexMatches input toMatchRegex = 
    let mutable hashtags:List<string> = List.Empty
    let regex = Regex(toMatchRegex)
    let matches = regex.Matches(input)
    for matchedWord in matches do  
        hashtags <- matchedWord.Groups.[1].Value::hashtags
    hashtags

let dt = new DataSet("DataSet")

let tweetsIdTable = dt.Tables.Add("TweetId");
tweetsIdTable.Columns.Add("Id", typeof<Guid>);
tweetsIdTable.PrimaryKey <- [|tweetsIdTable.Columns.["Id"]|]

let userTable = dt.Tables.Add("User");
userTable.Columns.Add("Id", typeof<string>);
userTable.Columns.Add("Password", typeof<string>);
//Primary key
userTable.PrimaryKey <- [|userTable.Columns.["Id"]|]

let tweetsTable = dt.Tables.Add("Tweet");
tweetsTable.Columns.Add("Id", typeof<Guid>);
tweetsTable.Columns.Add("UserId", typeof<string>);
tweetsTable.Columns.Add("Data", typeof<string>);
tweetsTable.Columns.Add("IsOriginal", typeof<Boolean>);
tweetsTable.Columns.Add("timestamp", typeof<DateTime>);
//Primary keys
let uc = UniqueConstraint("tweetsTablepk", [|tweetsTable.Columns.["Id"];tweetsTable.Columns.["UserId"]|], true);
tweetsTable.Constraints.Add(uc)
//
let register2tweetDbRelation = DataRelation("register2tweetDb",userTable.Columns.["Id"],tweetsTable.Columns.["userId"])
dt.Relations.Add(register2tweetDbRelation) |> ignore

let register2tweetDbRelation2 = DataRelation("register2tweetDb2",tweetsIdTable.Columns.["Id"],tweetsTable.Columns.["Id"])
dt.Relations.Add(register2tweetDbRelation2) |> ignore

let tweetMentionsTable = dt.Tables.Add("TweetDetails");
tweetMentionsTable.Columns.Add("Id", typeof<Guid>);
tweetMentionsTable.Columns.Add("Tag", typeof<String>); // Is will contain the tag, that is either userMention or hashTag data.
tweetMentionsTable.Columns.Add("TagType", typeof<int>); // HashTag or UserMention
let tweetMentionsTableDbRelation = DataRelation("tweetMentionsTable",tweetsIdTable.Columns.["Id"],tweetMentionsTable.Columns.["Id"])
dt.Relations.Add(tweetMentionsTableDbRelation) |> ignore


let subscribersTable = dt.Tables.Add("Subscribers");
subscribersTable.Columns.Add("UserId", typeof<string>);
subscribersTable.Columns.Add("SubscribedToUserId", typeof<string>);
let subscribersTableDbRelation1 = DataRelation("subscribersTableDbRelation1",userTable.Columns.["Id"], subscribersTable.Columns.["UserId"])
let subscribersTableDbRelation2 = DataRelation("subscribersTableDbRelation2",userTable.Columns.["Id"], subscribersTable.Columns.["SubscribedToUserId"])
dt.Relations.Add(subscribersTableDbRelation1) |> ignore
dt.Relations.Add(subscribersTableDbRelation2) |> ignore


let commaSeparateGuidValuesForQuery  (data:List<_>) : string = 
    "(" + String.Join(",", (data |> List.map(fun x -> "Convert('" + x.ToString() + "', 'System.Guid')"))) + ")"

let commaSeparateValuesForQuery  (data:List<_>) : string = 
    "(" + String.Join(",", (data |> List.map(fun x -> "'" + x.ToString() + "'"))) + ")"


let addUser userId password : Boolean =
    // printfn "USer name %A : Password %A" userId password
    let res =
        try
            lock userTable (fun () -> userTable.Rows.Add(userId, password)) |> ignore
            true
        with
        | :? ConstraintException ->
            false
    res

let isUserExists (userId:string) :bool =
    let foundRows = lock userTable  (fun () -> userTable.Select(String.Format("Id = '{0}'", userId)))
    foundRows.Length <> 0

let validateUser (userId:string) (password:string) : bool =
    let foundRows = lock userTable  (fun () -> userTable.Select(String.Format("Id = '{0}' AND Password = '{1}'", userId, password)))
    foundRows.Length <> 0

let addUserToLocalMap (userId:string) =
    try
        lock loggedInUser (fun () -> loggedInUser.AddOrUpdate(userId,"",fun key oval -> "")) |> ignore
        true
    with
    | :? ArgumentNullException ->
        false

let removeUserFromLocalMap (userId:string) = 
    try
        let mutable value = ""
        lock loggedInUser (fun () -> loggedInUser.TryRemove(userId,ref value)) |> ignore
        true
    with
    | :? ArgumentNullException ->
        false

let isActiveUser (userId:string) =
    // lock loggedInUser (fun () -> loggedInUser.ContainsKey(userId))
    loggedInUser.ContainsKey(userId)

let addTweet (tweetId:Guid) userId tweetData isOriginal = 
    //var input = "asdads sdfdsf #burgers, #rabbits dsfsdfds #sdf #dfgdfg";
    let hashtags = getRegexMatches tweetData hashtagRegex
    let userMentions = getRegexMatches tweetData userMentionRegex

    //printfn "Hashtags : %A" hashtags
    //printfn "usermentions : %A" userMentions
    try
        if isOriginal then
            lock tweetsIdTable (fun () -> tweetsIdTable.Rows.Add(tweetId)) |> ignore
        lock tweetsTable (fun()-> tweetsTable.Rows.Add(tweetId, userId, tweetData, isOriginal, DateTime.UtcNow)) |> ignore
        for hashtag in hashtags do
            lock tweetMentionsTable (fun () -> tweetMentionsTable.Rows.Add(tweetId, hashtag, (int)TagType.HashTag)) |> ignore

        for userMention in userMentions do
            lock tweetMentionsTable (fun () ->tweetMentionsTable.Rows.Add(tweetId, userMention, (int)TagType.UserMention)) |> ignore
        true
    with
    | :? InvalidConstraintException as ex->
        printfn "Addition of tweet failed with %A" ex
        false
    | :? ArgumentException  as ex->
        printfn "Argument Exception in addTweet method %A" ex
        false
    | :? InvalidCastException as ex ->
        printfn "Invalid Cast Exception in addTweet method %A" ex
        false
    | :? ConstraintException as ex ->
        printfn "Constraint Exception in addTweet method %A" ex
        false
    
let formatRowsInTweetsData (tweetrows:DataRow[]) = 
    tweetrows
        |> List.ofArray
        |> List.map(fun x ->
         {Id = Guid.Parse((string)x.ItemArray.[0]);
         UserId = (string) x.ItemArray.[1];
         Data = (string) x.ItemArray.[2]; 
         IsOriginal = Boolean.Parse((string)x.ItemArray.[3]);
         Time = DateTime.Parse((string)x.ItemArray.[4])}) 
        |> List.sortByDescending(fun y -> y.Time)
   
let getTweets (tweetIds:List<Guid>) =
    
    let foundRows = 
            if tweetIds.Length = 0 then
                [||]
            elif tweetIds.Length > 0 then
                let tweetsCommaSeparated = commaSeparateGuidValuesForQuery tweetIds 
                lock tweetsTable (fun () ->tweetsTable.Select(String.Format("Id in {0}", tweetsCommaSeparated)))
            else 
                lock tweetsTable (fun () ->tweetsTable.Select(String.Format("Id in '{0}'", tweetIds.[0])))
    
    if foundRows.Length = 0 then
        []
    else
        formatRowsInTweetsData foundRows

let getTweetsAlongWithUserId tweetId userId = 
    let foundRows = lock tweetsTable (fun () ->tweetsTable.Select(String.Format("Id = '{0}' AND  UserId  = '{1}'", tweetId, userId)))
    if foundRows.Length = 0 then
        []
    else
        formatRowsInTweetsData foundRows

let getTweetIdsByUserMention (userMention) : List<Guid> =
    let foundRows = lock tweetMentionsTable (fun () ->tweetMentionsTable.Select(String.Format("Tag = '{0}' AND TagType = {1}", userMention, (int)TagType.UserMention)))
    if foundRows.Length = 0 then
        []
    else
        foundRows
        |> List.ofArray
        |> List.map(fun x -> Guid.Parse((string)x.ItemArray.[0]))
 
let getTweetIdsByHashTag (hashTagIn:List<string>) : List<Guid>= 
    
    let foundRows = 
        let hashTag = hashTagIn
                    |> List.map(fun x -> x.TrimStart('#'))
        if hashTag.Length > 1 then
            let commaSeparatedVal = commaSeparateValuesForQuery hashTag
            //printfn "%A" commaSeparatedVal
            lock tweetMentionsTable (fun () ->tweetMentionsTable.Select(String.Format("Tag IN {0} AND TagType = {1}", commaSeparatedVal, (int)TagType.HashTag)))
        else 
            //printfn "%A" hashTag.[0]
            lock tweetMentionsTable (fun () ->tweetMentionsTable.Select(String.Format("Tag = '{0}' AND TagType = {1}", hashTag.[0], (int)TagType.HashTag)))
    
    if foundRows.Length = 0 then
        []
    else
        foundRows
        |> List.ofArray
        |> List.map(fun x ->  
                            // printfn "%A" x.ItemArray
                            Guid.Parse((string)(x.ItemArray.[0])))

let getTweetsByUserMention userMention = 
    let tweetIds = getTweetIdsByUserMention userMention
    getTweets tweetIds 

 
let getTweetsByHashTag (hashTag:List<string>) = 
    let tweetIds = getTweetIdsByHashTag hashTag
    getTweets tweetIds

let addSubscribedTo (userId:string) (subscribedTo:string) =
    try
        // Need to check if the same pair comes again
        lock subscribersTable (fun () ->subscribersTable.Rows.Add(userId,subscribedTo)) |> ignore
        true
    with
    | :? InvalidConstraintException ->
        printfn "Addition of subscribers failed with InvalidConstraintException"
        false
    | :? ArgumentException ->
        printfn "Argument Exception in addSubscribedTo method"
        false
    | :? InvalidCastException ->
        false

let getTweetByID (tweetId:Guid) =
    lock tweetsTable (fun () ->tweetsTable.Select(String.Format("Id = '{0}'", tweetId)))

let getSubscribesForUser (userId:string) =
    let foundRows = lock subscribersTable (fun () ->subscribersTable.Select(String.Format("SubscribedToUserId =  '{0}'", userId)))
    if foundRows.Length = 0 then
        []
    else
        foundRows
        |> List.ofArray
        |> List.map(fun x ->  (string)x.ItemArray.[0])

let tweetsByUserId (userId:string) =
    let foundRows = lock tweetsTable (fun () -> tweetsTable.Select(String.Format("UserId = '{0}'", userId)))
    if foundRows.Length = 0 then
        []
    else
        formatRowsInTweetsData foundRows

let tweetsByUserIdList (userList:List<string>) =
    // printfn "%A" userList
    if userList.Length = 0 then
        []
    else if userList.Length = 1 then
        tweetsByUserId (userList.Item(0))
    else
        try
            let tweetsCommaSeparated = commaSeparateValuesForQuery userList
            let foundRows = lock tweetsTable (fun () ->tweetsTable.Select(String.Format("UserId in {0}", tweetsCommaSeparated)))
            if foundRows.Length = 0 then
                []
            else
                formatRowsInTweetsData foundRows
        with
        | :? Exception as ex ->
            printfn "Exception while retrieving tweets - %A" ex
            []

let getSubscribedToForUser (userId:string) =
    let foundRows = lock subscribersTable (fun () ->subscribersTable.Select(String.Format("UserId =  '{0}'", userId)))
    if foundRows.Length = 0 then
        []
    else
        foundRows
        |> List.ofArray
        |> List.map(fun x ->  (string)x.ItemArray.[1])
