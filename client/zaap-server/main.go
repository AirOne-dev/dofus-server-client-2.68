// zaap-server : émulateur minimal du protocole Apache Thrift d'Ankama Zaap.
// Réutilise les handlers Thrift de DivaZaap (jordanamr/DivaZaap) extraits sans
// la GUI Wails. Démarre un serveur Thrift sur 127.0.0.1:<port>, attend que le
// client Dofus s'y connecte avec les bons gameName/instanceId/hash, puis lui
// répond OK pour qu'il passe au-delà du blocage "Ankama Launcher requis".
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strconv"
	"syscall"

	"divazaap/src/server"

	"github.com/apache/thrift/lib/go/thrift"
)

func main() {
	zaapPort := flag.Int("port", 4242,
		"Port TCP du serveur Zaap fake (correspond à --port= passé au client)")
	httpPort := flag.Int("http-port", 4243,
		"Port HTTP optionnel (sert /divazaap.json)")
	hash := flag.String("hash", "stump",
		"Hash partagé avec le client (--hash=)")
	instanceID := flag.Int("instance-id", 1,
		"Instance ID partagé avec le client")
	gameToken := flag.String("game-token", "stump",
		"Game token retourné au client lors de auth_getGameToken (= mot de passe Giny en clair)")
	login := flag.String("login", "",
		"Username Giny (retourné par userInfo_get → authManager.loginValidationAction.username)")
	authAddr := flag.String("auth-addr", "127.0.0.1:5555",
		"Adresse du serveur d'auth (utilisée dans /divazaap.json)")
	flag.Parse()

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

	// Pré-enregistre le client pour que le hash matche dès le 1er Connect().
	rs.Handler.Register(*gameToken, int32(*instanceID), *hash, *login)

	log.Printf("zaap-server listening on 127.0.0.1:%d (http :%d)",
		*zaapPort, *httpPort)
	log.Printf("hash=%s instanceID=%d login=%s gameToken=%s authAddr=%s",
		*hash, *instanceID, *login, *gameToken, *authAddr)

	// Sauve le PID dans un fichier si demandé via env (utile au launcher pour
	// le killer après).
	if pidFile := os.Getenv("ZAAP_PIDFILE"); pidFile != "" {
		_ = os.WriteFile(pidFile,
			[]byte(strconv.Itoa(os.Getpid())), 0o644)
	}

	// Bloque jusqu'à signal.
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig
	log.Println("zaap-server shutting down")
	rs.Stop()
}
