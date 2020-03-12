FROM mcr.microsoft.com/dotnet/core/sdk

RUN mkdir /mensa_scanner
COPY . /mensa_scanner

WORKDIR /mensa_scanner/MensaScanner

CMD dotnet run Program.cs
