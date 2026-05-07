// Émulateur minimal du protocole Apache Thrift Ankama Zaap.
// Repris de https://github.com/jordanamr/DivaZaap (handlers extraits sans
// la GUI Wails).
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"divazaap/src/server"

	"github.com/apache/thrift/lib/go/thrift"
)

func main() {
	zaapPort := flag.Int("port", 4242, "Port TCP du serveur Zaap")
	httpPort := flag.Int("http-port", 4243, "Port HTTP (sert /divazaap.json)")
	hash := flag.String("hash", "stump", "Hash partagé avec le client")
	instanceID := flag.Int("instance-id", 1, "Instance ID partagé avec le client")
	gameToken := flag.String("game-token", "stump",
		"Game token (= mot de passe Giny en clair)")
	login := flag.String("login", "",
		"Username Giny (retourné par userInfo_get)")
	authAddr := flag.String("auth-addr", "127.0.0.1:5555",
		"Adresse du serveur d'auth (sert dans /divazaap.json)")
	// --log-file plutôt que stdout : le launcher Windows fait Environment.Exit(0)
	// après le spawn, ce qui ferme les pipes anonymes hérités → broken pipe ici.
	logFile := flag.String("log-file", "",
		"Si fourni, append les logs dans ce fichier au lieu de stderr.")
	flag.Parse()

	if *logFile != "" {
		f, err := os.OpenFile(*logFile, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
		if err == nil {
			log.SetOutput(f)
		} else {
			log.Printf("could not open log file %s: %v (falling back to stderr)", *logFile, err)
		}
	}

	transportFactory := thrift.NewTTransportFactory()
	protocolFactory := thrift.NewTBinaryProtocolFactoryConf(&thrift.TConfiguration{})

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	rs, err := server.RunServer(
		transportFactory, protocolFactory,
		fmt.Sprintf("127.0.0.1:%d", *zaapPort),
		fmt.Sprintf("127.0.0.1:%d", *httpPort),
		*authAddr,
		ctx,
	)
	if err != nil {
		log.Fatalf("RunServer: %v", err)
	}

	// Pré-enregistrement : le hash matche dès le 1er Connect().
	rs.Handler.Register(*gameToken, int32(*instanceID), *hash, *login)

	log.Printf("zaap-server listening on 127.0.0.1:%d (http :%d)",
		*zaapPort, *httpPort)
	log.Printf("hash=%s instanceID=%d login=%s gameToken=%s authAddr=%s",
		*hash, *instanceID, *login, *gameToken, *authAddr)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
	log.Println("zaap-server shutting down")
	rs.Stop()
}
