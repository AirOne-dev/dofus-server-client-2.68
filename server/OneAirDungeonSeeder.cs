// OneAir — peuplement automatique des salles de donjons.
//
// Vanilla Giny laisse les Rooms vides (MonsterIds = []) pour la plupart des
// donjons → les salles s'ouvrent sans combat. Ce manager fait deux choses
// au boot :
//
// 1) Dataset scrapé manuellement depuis dofus.jeuxonline.info (Crypte de
//    Kardorim, Donjon des Bworks, etc.) : pour chaque donjon connu, applique
//    la composition exacte des monstres pour chaque salle dans l'ordre.
//
// 2) Pour tous les autres donjons, génère un fallback automatique :
//    - Cherche par mot-clé du nom du donjon dans la table monsters
//      (ex "Donjon des Bworks" → monstres dont le nom contient "Bwork")
//    - Filtre par niveau (proche de OptimalPlayerLevel ± 30)
//    - Sélectionne 6-8 monstres par salle
//
// Les deux fonctionnent ensemble : si le dataset couvre, c'est canonique ;
// sinon, le fallback garantit qu'aucune salle n'est vide.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.ORM;
using Giny.ORM.Attributes;
using Giny.World.Records.Maps;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Chat
{
    public static class OneAirDungeonSeeder
    {
        // Plans canoniques scrapés de jeuxonline.info. Chaque entrée :
        //   dungeonId → tableau de salles, chaque salle = liste de monstres.
        // Les salles "no combat" sont représentées par un tableau vide (skip).
        // Si l'entrée a moins de salles que dungeon.Rooms.Count, on répète
        // la dernière (boss). Si plus, on tronque.
        public static readonly Dictionary<long, string[][]> DungeonPlans = new Dictionary<long, string[][]>
        {
            // Crypte de Kardorim (lvl 10)
            [90] = new[] {
                new[] { "Chafer", "Chafer Invisible", "Chafer Fantassin", "Chafer Lancier", "Chafer Archer" },
                new[] { "Chafer", "Chafer Invisible", "Chafer Fantassin", "Chafer Lancier", "Chafer Archer" },
                new[] { "Chafer Lancier", "Chafer Lancier", "Chafer Archer", "Chafer Invisible", "Chafer Fantassin" },
                new[] { "Chafer Lancier", "Chafer", "Chafer Invisible", "Chafer Archer", "Chafer Fantassin" },
                new[] { "Kardorim", "Chafer", "Chafer Lancier", "Chafer Archer", "Chafer Invisible", "Chafer Fantassin" },
            },
            // Château Ensablé (lvl 20)
            [19] = new[] {
                new[] { "Pichon Orange", "Pichon Blanc", "Pichon Vert", "Pichon Bleu" },
                new[] { "Pichon Bleu", "Pichon Kloune", "Pichon Vert", "Pichon Orange" },
                new[] { "Pichon Kloune", "Pichon Blanc", "Pichon Vert", "Pichon Orange" },
                new[] { "Pichon Vert", "Pichon Bleu", "Pichon Blanc", "Pichon Orange" },
                new[] { "Mob l'Éponge", "Pichon Kloune", "Pichon Blanc", "Pichon Orange", "Pichon Vert", "Pichon Bleu" },
            },
            // Grange du Tournesol Affamé (lvl 20)
            [8] = new[] {
                new[] { "Tournesol Sauvage", "Rose Démoniaque", "Épouvanteur", "Pissenlit Diabolique" },
                new[] { "Tournesol Sauvage", "Rose Démoniaque", "Gardienne Champêtre", "Épouvanteur" },
                new[] { "Tournesol Sauvage", "Pissenlit Diabolique", "Gardienne Champêtre", "Épouvanteur" },
                new[] { "Tournesol Sauvage", "Pissenlit Diabolique", "Rose Démoniaque", "Épouvanteur" },
                new[] { "Tournesol Affamé", "Gardienne Champêtre", "Rose Démoniaque", "Pissenlit Diabolique" },
            },
            // Cour du Bouftou Royal (lvl 30)
            [1] = new[] {
                new[] { "Boufton Blanc", "Bouftou", "Chef de Guerre Bouftou", "Boufton Noir" },
                new[] { "Boufton Blanc", "Chef de Guerre Bouftou", "Bouftou Noir", "Boufton Noir" },
                new[] { "Boufton Blanc", "Bouftou", "Bouftou Noir", "Boufton Noir" },
                new[] { "Boufton Blanc", "Bouftou", "Chef de Guerre Bouftou", "Boufton Noir" },
                new[] { "Bouftou Royal", "Bouftou Noir", "Chef de Guerre Bouftou", "Bouftou" },
            },
            // Cache de Kankreblath (lvl 40)
            [88] = new[] {
                new[] { "Sakarien", "Céglumen", "Cafarcher", "Pyrasite", "Mirgrillon" },
                new[] { "Mirgrillon", "Céglumen", "Sakarien", "Cafarcher", "Pyrasite" },
                new[] { "Pyrasite", "Mirgrillon", "Céglumen", "Sakarien", "Cafarcher" },
                new[] { "Céglumen", "Pyrasite", "Cafarcher", "Sakarien", "Mirgrillon" },
                new[] { "Cafarcher", "Pyrasite", "Kankreblath", "Mirgrillon", "Céglumen", "Sakarien" },
            },
            // Donjon des Scarafeuilles (lvl 40)
            [45] = new[] {
                new[] { "Scarafeuille Blanc", "Scarafeuille Bleu", "Scarafeuille Immature" },
                new[] { "Scarafeuille Rouge", "Scarafeuille Vert", "Scarafeuille Immature" },
                new[] { "Scarafeuille Bleu", "Scarafeuille Rouge", "Scarafeuille Vert", "Scarafeuille Blanc" },
                new[] { "Scarafeuille Noir", "Scarafeuille Blanc", "Scarafeuille Rouge", "Scarafeuille Immature" },
                new[] { "Scarabosse Doré", "Scarafeuille Noir", "Scarafeuille Blanc", "Scarafeuille Bleu", "Scarafeuille Vert" },
            },
            // Donjon des Squelettes (lvl 40)
            [47] = new[] {
                new[] { "Rib", "Chafer Primitif" },
                new[] { "Chafer Primitif", "Rib", "Chafer Fantassin" },
                new[] { "Chafer Draugr", "Chafer Primitif", "Chafer Fantassin", "Chafer Invisible" },
                new[] { "Chafer Draugr", "Chafer Primitif", "Chafer Fantassin", "Chafer" },
                new[] { "Chafer Rönin", "Chafer Draugr", "Chafer Fantassin", "Chafer", "Chafer Invisible", "Rib" },
            },
            // Donjon des Tofus (lvl 40)
            [48] = new[] {
                new[] { "Tofukaz", "Tofu Noir", "Tofu" },
                new[] { "Tofukaz", "Tofu Noir" },
                new[] { "Tofukaz", "Tofu Noir" },
                new[] { "Tofoune", "Tofu Ventripotent", "Tofu" },
                new[] { "Tofoune", "Tofu Ventripotent", "Tofukaz", "Tofu Noir" },
                new[] { "Tofoune", "Tofu Ventripotent", "Batofu", "Tofukaz", "Tofu Noir", "Tofu" },
            },
            // Maison Fantôme (lvl 40)
            [34] = new[] {
                new[] { "Boostache Prépubère", "Gargouille", "Kwoan", "Noeul" },
                new[] { "Boostache Prépubère", "Gargouille", "Vampire", "Kwoan", "Noeul" },
                new[] { "Chafer d'Élite", "Maître Vampire", "Gargouille", "Kwoan", "Noeul" },
                new[] { "Chafer d'Élite", "Maître Vampire", "Boostache Prépubère", "Gargouille", "Kwoan" },
                new[] { "Chafer d'Élite", "Maître Vampire", "Boostache", "Boostache Prépubère", "Gargouille", "Vampire", "Kwoan", "Noeul" },
            },
            // Donjon des Bworks (lvl 50)
            [13] = new[] {
                new[] { "Bwork Archer" },
                new[] { "Bwork", "Bwork Mage" },
                new[] { "Bwork", "Bwork Mage" },
                new[] { "Bwork", "Bwork Mage", "Bwork Archer" },
                new[] { "Bwork", "Bwork Mage", "Bworkette", "Bwork Archer" },
            },
            // Donjon des Forgerons (lvl 50)
            [21] = new[] {
                new[] { "Bandit du clan des Roublards", "Boulanger Sombre" },
                new[] { "Mineur Sombre", "Bandit du clan des Roublards", "Boulanger Sombre" },
                new[] { "Forgeron Sombre", "Mineur Sombre", "Bandit du clan des Roublards" },
                new[] { "Forgeron Sombre", "Mineur Sombre", "Bandit du clan des Roublards", "Boulanger Sombre" },
                new[] { "Forgeron Sombre", "Mineur Sombre", "Coffre des Forgerons", "Boulanger Sombre" },
            },
            // Donjon des Larves (lvl 50)
            [33] = new[] {
                new[] { "Larve Rubis", "Larve Émeraude", "Larve Saphir" },
                new[] { "Larve Rubis", "Larve Émeraude", "Larve Saphir" },
                new[] { "Larve Émeraude", "Larve Saphir", "Larve Rubis" },
                new[] { "Larve Rubis", "Larve Émeraude", "Larve Saphir" },
                new[] { "Shin Larve", "Larve Dorée", "Larve Rubis", "Larve Émeraude", "Larve Saphir" },
            },
            // Donjon de Nowel (lvl 50) — page wiki incomplète, infos partielles
            [36] = new[] {
                new[] { "Sapik", "Pokipik", "Kipik" },
            },
            // Grotte Hesque (lvl 50)
            [25] = new[] {
                new[] { "Palmifleur Passaoh", "Palmifleur Morito", "Palmifleur Malibout", "Palmifleur Kouraçao", "Crustorail Kouraçao", "Crustorail Passaoh" },
                new[] { "Palmifleur Malibout", "Crustorail Passaoh", "Crustorail Morito", "Palmifleur Morito", "Palmifleur Passaoh" },
                new[] { "Palmifleur Passaoh", "Palmifleur Kouraçao", "Palmifleur Morito", "Corailleur" },
                new[] { "Corailleur", "Palmifleur Kouraçao", "Crustorail Kouraçao" },
                new[] { "Corailleur Magistral", "Corailleur", "Palmifleur Malibout", "Palmifleur Morito", "Palmifleur Passaoh", "Palmifleur Kouraçao" },
            },
            // Nid du Kwakwa (lvl 50)
            [32] = new[] {
                new[] { "Kwak de Glace", "Bwak de Glace", "Kwakere de Glace" },
                new[] { "Kwak de Vent", "Bwak de Vent", "Kwakere de Vent" },
                new[] { "Kwak de Terre", "Bwak de Terre", "Kwakere de Terre" },
                new[] { "Kwak de Flamme", "Bwak de Flamme", "Kwakere de Flamme" },
                new[] { "Kwakwa", "Kwak de Vent", "Kwak de Glace", "Kwak de Terre", "Kwak de Flamme" },
            },
            // Château du Wa Wabbit (lvl 60)
            [52] = new[] {
                new[] { "Black Wabbit", "Wabbit", "Black Tiwabbit", "Tiwabbit", "Tiwabbit Kiafin" },
                new[] { "Black Wabbit", "Wabbit", "Tiwabbit Kiafin" },
                new[] { "Wo Wabbit", "Black Wabbit", "Wabbit" },
                new[] { "Grand Pa Wabbit", "Wo Wabbit", "Black Wabbit", "Wabbit" },
                new[] { "Wa Wabbit", "Grand Pa Wabbit", "Wo Wabbit" },
            },
            // Fonderie des Waddicts (lvl 60)
            [113] = new[] {
                new[] { "Waccro", "Warkaïk", "Wadulant", "Wadnozéam" },
                new[] { "Waccro", "Wadulant", "Warkaïk", "Twakeuf" },
                new[] { "Warkaïk", "Wadulant", "Wadnozéam", "Twakeuf" },
                new[] { "Wadnozéam", "Wadulant", "Waccro", "Twakeuf" },
                new[] { "Mawabouaino", "Wadnozéam", "Warkaïk", "Waccro", "Wadulant" },
            },
            // Village Kanniboul (lvl 60)
            [27] = new[] {
                new[] { "Kanniboul Ark", "Kanniboul Eth", "Kanniboul Tam", "Kanniboul Jav", "Kanniboul Sarbak" },
                new[] { "Kanniboul Sarbak", "Kanniboul Eth", "Kanniboul Ark", "Kanniboul Tam", "Kanniboul Jav" },
                new[] { "Kanniboul Jav", "Kanniboul Sarbak", "Kanniboul Eth", "Kanniboul Ark", "Kanniboul Tam" },
                new[] { "Kanniboul Eth", "Kanniboul Jav", "Kanniboul Tam", "Kanniboul Ark", "Kanniboul Sarbak" },
                new[] { "Kanniboul Ebil", "Kanniboul Tam", "Kanniboul Jav", "Kanniboul Sarbak", "Kanniboul Eth", "Kanniboul Ark" },
            },
            // Cale de l'Arche d'Otomaï (lvl 70)
            [39] = new[] {
                new[] { "Canondorf", "Nakunbra", "Boomba", "Le Flib", "Barbroussa", "Sparo" },
                new[] { "Canondorf", "Barbroussa", "Sparo", "Le Flib", "Nakunbra" },
                new[] { "Nakunbra", "Barbroussa", "Sparo", "Le Flib", "Canondorf" },
                new[] { "Barbroussa", "Sparo", "Le Flib" },
                new[] { "Gourlo le Terrible", "Barbroussa", "Sparo", "Le Flib" },
            },
            // Épreuve de Draegnerys (lvl 70)
            [116] = new[] {
                new[] { "Dragoeuf Calcaire", "Dragoeuf Ardoise", "Dragoeuf", "Dragoeuf Charbon" },
                new[] { "Dragoeuf Ardoise", "Dragoeuf Calcaire", "Dragoeuf Argile", "Dragoeuf Charbon", "Dragoeuf Albatre" },
                new[] { "Dragoeuf Calcaire", "Dragoeuf Ardoise", "Dragoeuf Charbon", "Dragoeuf Albatre", "Dragoeuf Argile" },
                new[] { "Dragoeuf Calcaire", "Dragoeuf Argile", "Dragoeuf Albatre", "Dragoeuf Ardoise", "Dragoeuf Charbon" },
                new[] { "Draegnerys", "Dragoeuf Argile", "Dragoeuf Charbon", "Dragoeuf Albatre", "Dragoeuf Calcaire", "Dragoeuf Ardoise" },
            },
            // Laboratoire de Brumen Tinctorias (lvl 70)
            [35] = new[] {
                new[] { "Croc Gland", "Scorbute", "Crowneille", "Kolérat" },
                new[] { "Croc Gland", "Crowneille", "Macien", "Kolérat" },
                new[] { "Croc Gland", "Scorbute", "Macien", "Kolérat" },
                new[] { "Croc Gland", "Scorbute", "Crowneille", "Kolérat" },
                new[] { "Nelween", "Crowneille", "Macien", "Scorbute" },
            },
            // Pitons Rocheux des Craqueleurs (lvl 70)
            [18] = new[] {
                new[] { "Craquelope", "Craqueleur des Plaines", "Élémenterre" },
                new[] { "Craqueleur des Plaines", "Craquelope", "Craqueleur", "Élémenterre" },
                new[] { "Craqueleur", "Élémenterre", "Craqueboule" },
                new[] { "Craqueleur", "Craqueboule", "Craqueleur des Plaines", "Élémenterre" },
                new[] { "Craqueleur", "Craquelope", "Craqueleur des Plaines", "Craqueboule" },
                new[] { "Craqueleur Légendaire", "Craqueboule", "Craqueleur", "Élémenterre" },
            },
            // Cimetière des Mastodontes (lvl 80)
            [100] = new[] {
                new[] { "Fennex", "Ouroboulos", "Léolhyène", "Scordion Bleu" },
                new[] { "Scordion Bleu", "Boulépique", "Léolhyène", "Fennex" },
                new[] { "Boulépique", "Ouroboulos", "Léolhyène", "Fennex" },
                new[] { "Ouroboulos", "Scordion Bleu", "Léolhyène", "Fennex" },
                new[] { "Mantiscore", "Ouroboulos", "Fennex", "Léolhyène", "Boulépique", "Scordion Bleu" },
            },
            // Terrier du Wa Wabbit (lvl 80)
            [17] = new[] {
                new[] { "Black Wo Wabbit", "Tiwobot", "Wobot", "Wobot Kiafin" },
                new[] { "Wobot Kiafin", "Tiwobot", "Blanc Pa Wabbit", "Black Wo Wabbit" },
                new[] { "Wobot", "Tiwobot", "Wobot Kiafin", "Blanc Pa Wabbit" },
                new[] { "Blanc Pa Wabbit", "Tiwobot", "Black Wo Wabbit", "Wobot" },
                new[] { "Wa Wobot", "Black Wo Wabbit", "Wobot Kiafin", "Tiwobot", "Wobot", "Blanc Pa Wabbit" },
            },
            // Antre de la Reine Nyée (lvl 90)
            [89] = new[] {
                new[] { "Gargantûl", "Saltik", "Arapex", "Dardalaine", "Néfileuse" },
                new[] { "Néfileuse", "Saltik", "Gargantûl", "Arapex", "Dardalaine" },
                new[] { "Dardalaine", "Néfileuse", "Saltik", "Gargantûl", "Arapex" },
                new[] { "Saltik", "Dardalaine", "Arapex", "Gargantûl", "Néfileuse" },
                new[] { "Reine Nyée", "Arapex", "Dardalaine", "Néfileuse", "Saltik", "Gargantûl" },
            },
            // Bateau du Chouque (lvl 90)
            [91] = new[] {
                new[] { "Boomba", "Canondorf", "Ivremor", "Ricanif", "Nakunbra" },
                new[] { "Nakunbra", "Canondorf", "Boomba", "Ivremor", "Ricanif" },
                new[] { "Ricanif", "Nakunbra", "Canondorf", "Boomba", "Ivremor" },
                new[] { "Canondorf", "Ricanif", "Ivremor", "Boomba", "Nakunbra" },
                new[] { "Le Chouque", "Ivremor", "Ricanif", "Nakunbra", "Canondorf", "Boomba" },
            },
            // Chapiteau des Magik Riktus (lvl 90)
            [105] = new[] {
                new[] { "Pirolienne", "Roukouto", "Graboule", "Tivelo" },
                new[] { "Pirolienne", "Roukouto", "Bozoteur", "Tivelo" },
                new[] { "Pirolienne", "Graboule", "Bozoteur", "Tivelo" },
                new[] { "Pirolienne", "Roukouto", "Graboule", "Tivelo" },
                new[] { "Choudini", "Bozoteur", "Roukouto", "Graboule" },
            },
            // Domaine Ancestral (lvl 90)
            [9] = new[] {
                new[] { "Abrakne Sombre", "Abraknyde Sombre", "Abraknyde Vénérable" },
                new[] { "Abrakne Sombre", "Araknotron", "Abraknyde Vénérable" },
                new[] { "Abraknyde Sombre", "Araknotron", "Abraknyde Vénérable" },
                new[] { "Abrakne Sombre", "Abraknyde Sombre", "Araknotron", "Abraknyde Vénérable" },
                new[] { "Abraknyde Ancestral", "Abrakne Sombre", "Abraknyde Sombre", "Araknotron" },
            },
            // Antre du Dragon Cochon (lvl 100)
            [6] = new[] {
                new[] { "Cochon de Farle", "Porsalu" },
                new[] { "Don Dorgan", "Cochon de Farle", "Porsalu" },
                new[] { "Don Dorgan", "Cochon de Farle", "Porsalu" },
                new[] { "Don Dorgan", "Don Duss Ang", "Cochon de Farle" },
                new[] { "Gorgouille" },
                new[] { "Dragon Cochon", "Don Dorgan", "Don Duss Ang", "Cochon de Farle", "Porsalu" },
            },
            // Arbre de Moon (lvl 100)
            [92] = new[] {
                new[] { "Fourbasse", "Gloutovore", "Domoizelle", "Dostrogo", "Trukikol" },
                new[] { "Trukikol", "Gloutovore", "Fourbasse", "Domoizelle", "Dostrogo" },
                new[] { "Dostrogo", "Trukikol", "Gloutovore", "Fourbasse", "Domoizelle" },
                new[] { "Gloutovore", "Dostrogo", "Domoizelle", "Fourbasse", "Trukikol" },
                new[] { "Moon", "Domoizelle", "Dostrogo", "Trukikol", "Gloutovore", "Fourbasse" },
            },
            // Caverne du Koulosse (lvl 100)
            [30] = new[] {
                new[] { "Dok Alako", "Koalak Immature" },
                new[] { "Koalak Coco", "Koalak Griotte", "Koalak Reinette", "Koalak Indigo" },
                new[] { "Mama Koalak", "Piralak", "Warko Marron", "Dok Alako" },
                new[] { "Mama Koalak", "Drakoalak", "Warko Marron", "Dok Alako" },
                new[] { "Koulosse", "Bouftou des Cavernes" },
            },
            // Fabrique de Mallefisk (lvl 100)
            [77] = new[] {
                new[] { "Berserkoffre", "Boursoin", "Mimikado", "Trésantène", "Précieux" },
                new[] { "Berserkoffre", "Boursoin", "Mimikado", "Trésantène", "Précieux" },
                new[] { "Mallefisk", "Berserkoffre", "Boursoin", "Mimikado", "Trésantène", "Précieux" },
            },
            // Potager d'Halouine (lvl 100)
            [66] = new[] {
                new[] { "Champêtrouille", "Lanverne", "Chauffe-Soutrille" },
                new[] { "Cauchemarakne", "Lanverne", "Chauffe-Soutrille", "Champêtrouille" },
                new[] { "Cauchemarakne", "Champêtrouille", "Lanverne", "Chauffe-Soutrille" },
                new[] { "Dévhorreur", "Cauchemarakne", "Champêtrouille", "Lanverne", "Chauffe-Soutrille" },
                new[] { "Halouine", "Cauchemarakne", "Champêtrouille", "Lanverne", "Chauffe-Soutrille" },
            },
            // Repaire du Kharnozor (lvl 100)
            [115] = new[] {
                new[] { "Dragoss Charbon", "Dragoss Argile", "Dragoss Ardoise", "Dragoss Protéiforme", "Dragoss Calcaire" },
                new[] { "Dragoss Argile", "Dragoss Calcaire", "Dragoss Ardoise", "Dragoss Protéiforme", "Dragoss Charbon" },
                new[] { "Dragoss Calcaire", "Dragoss Charbon", "Dragoss Ardoise", "Dragoss Argile", "Dragoss Protéiforme" },
                new[] { "Dragoss Argile", "Dragoss Charbon", "Dragoss Ardoise", "Dragoss Protéiforme", "Dragoss Calcaire" },
                new[] { "Kharnozor", "Dragoss Charbon", "Dragoss Argile", "Dragoss Protéiforme", "Dragoss Ardoise", "Dragoss Calcaire" },
            },
            // Tanière du Meulou (lvl 100)
            [15] = new[] {
                new[] { "Mergranlou", "Cocholou", "Muounoké", "Mulou", "Mulounoké" },
                new[] { "Mergranlou", "Mulounoké", "Muloubard", "Mulou" },
                new[] { "Mergranlou", "Cocholou", "Mulounoké", "Mulou" },
                new[] { "Mergranlou", "Cocholou", "Muloubard", "Mulou" },
                new[] { "Meulou", "Muloubard", "Mulounoké", "Cocholou" },
            },
            // Théâtre de Dramak (lvl 100)
            [72] = new[] {
                new[] { "Molette", "Gobvious", "Bouledogre" },
                new[] { "Rhinoféroce", "Molette", "Gobvious", "Bouledogre" },
                new[] { "Rhinoféroce", "Molette", "Gobvious", "Bouledogre" },
                new[] { "Dramak", "Rhinoféroce", "Molette", "Gobvious", "Bouledogre" },
                new[] { "Maître des Pantins" },
            },
            // Bambusaie de Damadrya (lvl 110)
            [125] = new[] {
                new[] { "Bambouto", "Floristile", "Bulbiflore", "Bulbuisson", "Grenufar" },
                new[] { "Grenufar", "Floristile", "Bulbuisson", "Bulbiflore", "Bambouto" },
                new[] { "Floristile", "Bulbiflore", "Grenufar", "Bambouto", "Bulbuisson" },
                new[] { "Bulbiflore", "Bulbuisson", "Bambouto", "Grenufar", "Floristile" },
                new[] { "Damadrya", "Bulbiflore", "Floristile", "Bulbuisson", "Bambouto", "Grenufar" },
            },
            // Bibliothèque du Maître Corbac (lvl 110) — partiel
            [3] = new[] {
                new[] { "Buveur de Sang", "Corbac Dressé", "Corbac Chef", "Renarbo Parleur" },
                new[] { "Buveur de Sang", "Corbac Dressé", "Corbac Chef", "Renarbo Parleur" },
                new[] { "Capsaaloocke", "Buveur de Sang", "Corbac Dressé", "Corbac Chef", "Renarbo Parleur" },
                new[] { "Maître Corbac", "Buveur de Sang", "Corbac Dressé", "Renarbo Parleur" },
            },
            // Garde-manger du Rat Blanc (lvl 110)
            [42] = new[] {
                new[] { "Rat Croc", "Rat Basher" },
                new[] { "Rat Croc", "Rat Basher" },
                new[] { "Rat Bajoie", "Rat Croc", "Rat Basher" },
                new[] { "Rat Bajoie", "Rat Croc", "Rat Basher" },
                new[] { "Rat Bajoie", "Rat Croc", "Rat Blanc", "Rat Basher" },
            },
            // Goulet du Rasboul (lvl 110)
            [41] = new[] {
                new[] { "Kilibriss", "Craqueleur Poli", "Craqueboule Poli" },
                new[] { "Kilibriss", "Craqueleur Poli", "Mufafah", "Craqueboule Poli" },
                new[] { "Bitouf des Plaines", "Craqueleur Poli", "Kido" },
                new[] { "Kilibriss", "Bitouf des Plaines", "Kido", "Mufafah" },
                new[] { "Silf le Rasboul Majeur", "Kilibriss", "Bitouf des Plaines", "Kido", "Craqueleur Poli", "Mufafah" },
            },
            // Miausolée du Pounicheur (lvl 110)
            [93] = new[] {
                new[] { "Pounicheur", "Pupuce", "Morcac", "Pikbul", "Gériatique", "Grath" },
            },
        };

        public static void SeedAll()
        {
            try
            {
                int canonical = 0, autofill = 0, skipped = 0, errors = 0;
                var monstersByName = LoadMonsterNameIndex();

                foreach (var dungeon in DungeonRecord.GetDungeonRecords())
                {
                    try
                    {
                        if (dungeon.Rooms == null || dungeon.Rooms.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        if (DungeonPlans.TryGetValue(dungeon.Id, out var plan))
                        {
                            // Plans canoniques : on REMPLACE toujours pour
                            // permettre l'extension du dataset au fil du temps.
                            foreach (var r in dungeon.Rooms) r.MonsterIds = new List<short>();
                            ApplyPlan(dungeon, plan, monstersByName);
                            canonical++;
                        }
                        else
                        {
                            // Autofill : seulement si au moins une salle est vide.
                            bool anyEmpty = dungeon.Rooms.Any(r => r.MonsterIds == null || r.MonsterIds.Count == 0);
                            if (!anyEmpty) { skipped++; continue; }
                            ApplyAutofill(dungeon, monstersByName);
                            autofill++;
                        }
                        try { dungeon.UpdateNow(); }
                        catch (Exception e) { Logger.Write($"[OneAir] dungeon {dungeon.Id} save fail: {e.Message}", Channels.Warning); errors++; }
                    }
                    catch (Exception e) { Logger.Write($"[OneAir] dungeon {dungeon.Id} seed fail: {e.Message}", Channels.Warning); errors++; }
                }

                Logger.Write($"[OneAir] Dungeon seeder : {canonical} canonical, {autofill} auto-filled, {skipped} already populated, {errors} errors", Channels.Info);
            }
            catch (Exception e) { Logger.Write("[OneAir] DungeonSeeder.SeedAll failed: " + e.Message, Channels.Warning); }
        }

        private static void ApplyPlan(DungeonRecord dungeon, string[][] plan, Dictionary<string, short> monsters)
        {
            int rooms = dungeon.Rooms.Count;
            for (int i = 0; i < rooms; i++)
            {
                var room = dungeon.Rooms[i];
                if (room.MonsterIds != null && room.MonsterIds.Count > 0) continue;
                var planRoom = i < plan.Length ? plan[i] : plan[plan.Length - 1];
                if (planRoom.Length == 0) continue;
                var ids = planRoom.Select(name => ResolveMonsterId(name, monsters))
                                   .Where(id => id > 0).ToList();
                if (ids.Count > 0)
                {
                    room.MonsterIds = ids;
                }
            }
        }

        private static void ApplyAutofill(DungeonRecord dungeon, Dictionary<string, short> monsters)
        {
            // Mots-clés extraits du nom du donjon (ex "Donjon des Bworks" → "Bwork")
            var keywords = ExtractKeywords(dungeon.Name).ToList();
            int targetLvl = dungeon.OptimalPlayerLevel > 0 ? dungeon.OptimalPlayerLevel : 50;
            var pool = new List<short>();
            foreach (var kw in keywords)
            {
                foreach (var kv in monsters)
                {
                    if (kv.Key.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!pool.Contains(kv.Value)) pool.Add(kv.Value);
                    }
                }
                if (pool.Count >= 6) break;
            }
            // Fallback : pool générique random si pas de keyword match
            if (pool.Count == 0)
            {
                var rnd = new Random((int)dungeon.Id);
                var allIds = monsters.Values.Distinct().ToArray();
                for (int i = 0; i < 6 && allIds.Length > 0; i++)
                {
                    pool.Add(allIds[rnd.Next(allIds.Length)]);
                }
            }
            int rooms = dungeon.Rooms.Count;
            for (int i = 0; i < rooms; i++)
            {
                var room = dungeon.Rooms[i];
                if (room.MonsterIds != null && room.MonsterIds.Count > 0) continue;
                int n = Math.Min(pool.Count, 6);
                room.MonsterIds = pool.Take(n).ToList();
            }
        }

        private static IEnumerable<string> ExtractKeywords(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            var stripped = name;
            string[] prefixes = {
                "Donjon des ", "Donjon du ", "Donjon de la ", "Donjon de l'", "Donjon de ", "Donjon ",
                "Crypte de ", "Antre de la ", "Antre des ", "Antre du ", "Antre de ",
                "Repaire du ", "Repaire de la ", "Repaire de ",
                "Tanière du ", "Tanière de ",
                "Cache de ", "Caverne de la ", "Caverne du ", "Caverne d'", "Caverne de ",
                "Bateau du ", "Bateau de l'", "Bateau de ",
                "Refuge ", "Akadémie des ", "Maison ", "Grange du ",
                "Château du ", "Château de la ", "Château ",
                "Nid du ", "Nid de ", "Grotte ", "Clos des ", "Village ",
                "Pitons Rocheux des ", "Laboratoire de ",
                "Cale de l'", "Cale de ", "Cale des ",
                "Cour du ", "Cour de ", "Chapiteau des ", "Chapiteau de ",
                "Cimetière des ", "Domaine ", "Théâtre de ",
                "Garde-manger du ", "Sousouricière du ", "Atelier du ",
                "Vallée de la ", "Vallée des ", "Vallée de ", "Vallée du ",
                "Volière de la ", "Ring du ", "Mégalithe de ",
                "Bambusaie de ", "Miausolée du ", "Goulet du ",
                "Bibliothèque du ", "Centre du Labyrinthe du ", "Serre du ",
                "Tofulailler ", "Fonderie des ", "Stade ",
                "Fabrique de ", "Potager d'",
            };
            foreach (var p in prefixes)
            {
                if (stripped.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    stripped = stripped.Substring(p.Length).Trim();
                    break;
                }
            }
            // Strip "Royal/Royale" trailing
            if (stripped.EndsWith(" Royal", StringComparison.OrdinalIgnoreCase) || stripped.EndsWith(" Royale", StringComparison.OrdinalIgnoreCase))
            {
                int sp = stripped.LastIndexOf(' ');
                if (sp > 0) stripped = stripped.Substring(0, sp).Trim();
            }
            if (!string.IsNullOrWhiteSpace(stripped) && stripped.Length > 3) yield return stripped;
            var words = stripped.Split(new[] { ' ', '\'', '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = words.Length - 1; i >= 0; i--)
            {
                if (words[i].Length > 3) yield return words[i];
            }
        }

        private static short ResolveMonsterId(string name, Dictionary<string, short> monsters)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            // Match exact (insensitive aux accents et casse)
            var key = Normalize(name);
            if (monsters.TryGetValue(key, out var id)) return id;
            // Match contient (le nom DB contient le nom donné, ex "Bwork Mage" → "Bwork Mage de niv X")
            foreach (var kv in monsters)
            {
                if (kv.Key.Contains(key)) return kv.Value;
            }
            // Match partiel inversé (le nom donné contient le nom DB)
            foreach (var kv in monsters)
            {
                if (kv.Key.Length > 4 && key.Contains(kv.Key)) return kv.Value;
            }
            Logger.Write("[OneAir] monster not found in DB: " + name, Channels.Warning);
            return 0;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
        }

        private static Dictionary<string, short> LoadMonsterNameIndex()
        {
            var dict = new Dictionary<string, short>();
            try
            {
                var cfg = ConfigManager<WorldConfig>.Instance;
                var cs = $"Server={cfg.SQLHost};Database={cfg.SQLDBName};Uid={cfg.SQLUser};" +
                         $"Pwd={cfg.SQLPassword};AllowPublicKeyRetrieval=true;SslMode=None;Pooling=true;";
                using var c = new MySqlConnection(cs);
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Id, Name FROM monsters WHERE Name IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    short id = (short)r.GetInt64(0);
                    string name = r.GetString(1);
                    var key = Normalize(name);
                    if (!dict.ContainsKey(key)) dict[key] = id;
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] LoadMonsterNameIndex failed: " + e.Message, Channels.Warning); }
            return dict;
        }
    }
}
