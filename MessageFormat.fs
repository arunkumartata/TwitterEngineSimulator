module MessageFormat
open System
open System.Security.Cryptography
let SUCCESS = 200
let USEREXISTS = 409
let FAILED = 500
let FORBIDDEN = 403 
type TagType = HashTag = 0 | UserMention = 1 | Subscribed = 2
type ApiCall = Login = 0 | Logout = 1 | QueryByHash = 2 | Subscribe = 3 | Tweet = 4 | Register = 5 | QueryByMentions = 6 | QueryBySubs = 7
type RestCallType = GET = 0 | PUT = 1 | POST = 2 
type CredentialsMessage ={RequestId:Guid;UserId:string;Password:string}
type ReplyMessage2 = {Status:int;Message:string;}
type ReplyMessage = {RequestId:Guid;Status:int;Message:string;}
type SubscribeMessage = {RequestId:Guid;UserId:string;SubscribedToId:string}
type TweetMessage = {RequestId:Guid;UserId:string;Tweet:string}
type ReTweetMessage ={RequestId:Guid;UserId:string;TweetId:Guid}
type QueryMessage = {RequestId:Guid;UserId:string;Data:string; QueryType:TagType}
type LogoutMessage = {RequestId:Guid;UserId: string}
// type QueryByHashTagMessage = {UserId:string;HashTag:string}
// type QueryByUserMentionsMessage = {UserId:string; UserMentions:string}
// type QueryTweets = {UserId:string} // Type can be 0 - mentioned, 1-subscribedTo
type TweetData = {Id:Guid; UserId:string;Data:string; IsOriginal:Boolean; Time:DateTime}
type TweetsResponse ={RequestId:Guid;Status:int;Content:List<TweetData>}
type RSAParamMessage ={RequestId:Guid;UserId:string;PublicKey:RSAParameters}

//challenge
type ChallengeMessage ={RequestId:Guid;UserId:string;ChallengeKey:byte[]}
type ChallengeResponse={RequestId:Guid;UserId:string;ChallengeKey:byte[];Time:int64;SignedData:byte[]}

//User messages
type SubscriberInitiateMessage= {ListToSubscribe:Set<int>}

//type RestApiRequest = {RequestMethod:RestCallType; RequestURL:string;Body:string}