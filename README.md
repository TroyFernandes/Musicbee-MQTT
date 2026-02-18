# MusicBee MQTT Plugin
Send info of the current playing song to a MQTT Server. 

A visual example of what is sent (MQTT Explorer):
```
musicbee
    ➤ player
        status = online
        state = playing
        output_devices = ["Primary Sound Driver", "Speakers"]
        output_device = Primary Sound Driver
        progress = 99
        volume = 73
        file = C:\Users\Troy\Music\Main\SZA\SOS\Far.flac
        position = 232
        shuffle = true
        repeat = off
        muted = false
    ➤ song
        album = SOS
        albumart = /9j/4AAQSkZJRgABAQEBLAEsAAD/2wBDAAgGBgcGBQg ...
        title = Far
        artist = SZA
        info = FLAC 44.1 kHz, 1502k
        duration = 264
        track = 22
        albumartist = SZA
        year = February 6, 2026
        content_type = music
        genre = Hip Hop
    command = {"command": "pause"}
```

# How to use

The plugin needs info about your mqtt server (ip, port, creds), and uses a persistent file to save this info. **The plugin wont create the file automatically.**

1. Add the ```mb_MQTT.dll``` to your musicbee plugins folder

Heres how you can create that file.

1. Go to the musicbee appdata folder. On regular installations it should be the path: ```C:\Users\Troy\AppData\Roaming\MusicBee``` (troy in this instance is your account username)

    On portable installs it should be ```C:\Users\Troy\Desktop\MusicbeePortable\MusicBee\AppData```

2. Create a file in the above directory called ```MB_MQTT_Settings.xml``` with the following content:
    ```
    <settings>
        <server_address>192.168.1.157</server_address>
        <server_port>1883</server_port>
        <server_username>username</server_username>
        <server_password>password</server_password>
    </settings>
    ```
    server_address = your mqtt host IP

    server_port = your mqtt host port (usually is 1883)

    server_username = mqtt username (can be blank)

    server_password = mqtt password (can be blank)

    NOTE: The credentials can be blank, but dont delete the entries

# How to Build

1. Clone the repo.

2. Open the .sln file.

3. Change to "Release" 

4. Build the project. The output files will be in bin/release

**Issues you might encounter building the project**

Since this project outputs an assembly (.dll) normally the way you debug/develop is by setting a startup project on debug (in this case Musicbee.exe) and copying the build output to the application folder.

1. The main problem you might encounter is the post-build event copy. In visual studio, right click the project (CSharpDLL) and choose properties -> go to Build Events, and either change or remove the Post-build event line. If you remove it, you have to manually copy the files to the application plugin folder.

2. If you're just building for release, you wont encounter this issue, but for debugging you have the set the startup project to a Musicbee Executable.

    In the visual studio solution explorer you can right click the MusicBee entry, go to properties, and change the path. If it's not there you can right click the project (CSharpDLL) and add an existing item (Musicbee.exe)

# Some Oddities

1. I use logarithmic volume scaling in Musicbee so when controlling the volume externally (i.e from Homeassistant), the mapping might not be the same. It might seem the volume control isn't working, but it is, the values are just being interpreted differently.

# Extra notes

Originally I wasn't going to publish this since I made this plugin very quickly to have a way to test my other plugin I was developing for Homeassistant [hass-mqtt-mediaplayer](https://github.com/TroyFernandes/hass-mqtt-mediaplayer)

I then lost the code for this projecta long time ago, but found a backup I had recently. The NuGet MQTT package changed within the few years so I just spent some time updating it.

This project is by no means done and there is a few more things I'd like to do/add, but I don't have the time. A few people now have requested this plugin over the years so that's why I'm uploading this as it is.

I'm gonna say the classic line: "It works for me"

This is probably the one and only commit this project will get for now. But please feel free to fork the repo and add the changes you need. The code is very simple to follow along.

Thanks :)