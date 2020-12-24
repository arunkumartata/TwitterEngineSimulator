# TwitterEngineSimulator

Program Execution:
   1. Execute 'dotnet build' to generate messageformat dll which is shared between client and server.
   2. Execute below commands for server and client ( you can use websocket plugin as well)
        Server: 'dotnet fsi --langversion:preview .\ServerWithSocket-bonus.fsx'
        Client: 'dotnet fsi --langversion:preview .\Client-bonus.fsx'
   
   Server URL to connect: 'ws://127.0.0.1:8080/api'
   
   When the client program is executed it prompts messages to perform respective sub API's and prompts to enter 
   the sub API name followed by respective data required if any to send message to server.

Implementation details:
   1. REST API:
        We have used REST API structure where client sends a message that has RequestType:<method>, RequestURL:<uri> and Body:<Json>.
   2. Server(ServerWithSocket*.fsx file):
        We have used Suave framework to creates websockets and expose API for outside world. In this file we 
        have created method that accepts connection based on the api and creates a websocket per client which
        will be active for communication till the client closes the websocket. 
        Upon accepting the websocket connection from client we have created an proxyactor which is used to send messages 
        back to client at any time. This proxyactor is also used for live tweets where server keeps track of proxyactorref's for active users.
        This proxyactor after receiving messages from websocket stub it verifies and deserialized the jsonbody to respective message type and
        sends the data to respective actor which is implemented in 'TwitterEngine.fsx'. When client closes the socket the server socket stub sends
        poisonpill to respective proxyactor as well.
   3. User(Client*.fsx file):
        We have used .Net frameworks clientwebsocket to connect to server api. To execute multiple clients we need to execute 
        client program in different terminals which resembles each user. For user we have provided a list of sub API's available 
        that is printed at the start of the program. Based on the sub API that user selects, it prompts to enter required data which will be 
        used to construct the message body and serializes and wraps in REST like structure which was mentioned above and sends on the connected
        socket.
        In parallel with requesting inputs from user, this program/file checks for any live tweets or messages on the socket from server and prints
        these to console.
   4. API details:
        a. Server:
            Server exposes '/api' to connect from outside world. Sub API's/Sub URI's that are allowed and need to be sent in REST message are as follows
            1. register - POST
            2. login - POST
            3. logout - POST
            4. subscribe - POST
            5. tweet - POST
            6. retweet - POST (uses and requires 'tweetId' rather than message)
            7. query (hashtag|mentions|subscribers) - GET
            8. seckeyupdate - POST (bonus part)
            9. challengeresponse - POST (bonus part)
        b. Client
            The same subapis mentioned above will be prompted to user at the program execution start except for 'seckeyupdate' which is 
            used as 'updatesecuritykey'. There is sub message 'quit' which can be used to terminate the client program.

   5. To Share RSA-2048 public key we have added extra API 'seckeyupdate' (server) available for client as 'updatesecuritykey'(client)
    which can be executed after register to update the public key at server side.
    
   6. Once the public key is updated the server mandates to login using the challenge key and then verifies the signed data sent from client to login. The key is valid for 1sec and if the Client replies late the login will be failed.
    
   7. Data Access layer (DAO.fsx):
      It uses .Net frameworks Datatables to maintain in-memory database and respective utility methods to interact with the tables.
